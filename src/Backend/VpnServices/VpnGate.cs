namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Keeps runs that use the VPN and region switches apart: a switch restarts the tunnel, which would cut off a run using
/// it. Any number of VPN runs share the gate, and a switch needs it to itself.
/// <see cref="Services.WatchRunWorker"/> holds a share for each run of a watch with <c>UseVpn</c>, with
/// <see cref="TryEnterRun"/>, and <see cref="VpnRotationService"/> holds it alone from asking gluetun to switch until the
/// new tunnel is up, with <see cref="QueueSwitch"/>.
/// </summary>
/// <remarks>
/// Switches go first: once one is queued, no new run gets a share until every queued switch is done, so runs can't keep
/// a switch waiting for good, and a switch waits only for the runs already in progress. Runs don't queue: one that can't
/// enter is passed over, and tried again later.
/// </remarks>
public sealed class VpnGate
{
    private readonly Lock _lock = new();

    // Switches waiting for the gate, oldest first. Only those queued with WaitAsync, so the gate never goes to a switch
    // that isn't ready for it, such as a failover waiting for another switch to finish
    private readonly LinkedList<Waiter> _waiters = [];

    private int _runs;
    private int _queuedSwitches;
    private SwitchTicket? _switch;

    /// <summary>
    /// Whether a run can't enter now, since a switch is in progress or queued.
    /// </summary>
    public bool HoldsUpRuns
    {
        get
        {
            lock (_lock)
            {
                return HoldsUpRunsLocked;
            }
        }
    }

    // Called with the lock held
    private bool HoldsUpRunsLocked => _switch is not null || _queuedSwitches > 0;

    /// <summary>
    /// How many runs hold a share.
    /// </summary>
    public int Runs
    {
        get
        {
            lock (_lock)
            {
                return _runs;
            }
        }
    }

    /// <summary>
    /// Takes a share for a run, unless a switch is in progress or queued. Dispose it when the run ends.
    /// </summary>
    /// <returns>The share, or <see langword="null"/> if the run must wait.</returns>
    public IDisposable? TryEnterRun()
    {
        lock (_lock)
        {
            if (HoldsUpRunsLocked)
            {
                return null;
            }

            _runs++;
            return new RunShare(this);
        }
    }

    /// <summary>
    /// Queues a switch: from now on no new run enters, though the switch doesn't have the gate until
    /// <see cref="SwitchTicket.WaitAsync"/> says so. Dispose the ticket when the switch is done or given up.
    /// </summary>
    public SwitchTicket QueueSwitch()
    {
        lock (_lock)
        {
            _queuedSwitches++;
            return new SwitchTicket(this);
        }
    }

    private void LeaveRun()
    {
        lock (_lock)
        {
            _runs--;
            GrantNextSwitch();
        }
    }

    private Task WaitForSwitchAsync(SwitchTicket ticket, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ticket.State != SwitchState.Queued || ticket.Waiter is not null)
            {
                throw new InvalidOperationException("The switch has already waited for the gate, or is done.");
            }

            var waiter = new Waiter(ticket);
            ticket.Waiter = waiter;
            _waiters.AddLast(waiter.Node);
            GrantNextSwitch();
            if (waiter.Completion.Task.IsCompleted)
            {
                return waiter.Completion.Task;
            }

            waiter.Cancellation = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    if (waiter.Node.List is not null)
                    {
                        _waiters.Remove(waiter.Node);
                        ticket.Waiter = null;
                        waiter.Completion.TrySetCanceled(cancellationToken);
                    }
                }
            });
            return waiter.Completion.Task;
        }
    }

    /// <summary>
    /// Raised when the last switch in progress or queued is done, so runs held up by switches can enter now.
    /// Raised outside the gate's lock, on the thread that ended the switch; handlers must be quick and not throw.
    /// </summary>
    public event Action? RunsMayEnter;

    private void EndSwitch(SwitchTicket ticket)
    {
        bool runsMayEnter;
        lock (_lock)
        {
            if (!EndSwitchLocked(ticket))
            {
                return;
            }

            runsMayEnter = !HoldsUpRunsLocked;
        }

        if (runsMayEnter)
        {
            RunsMayEnter?.Invoke();
        }
    }

    // Called with the lock held. Returns false if the switch was done already
    private bool EndSwitchLocked(SwitchTicket ticket)
    {
        switch (ticket.State)
        {
            case SwitchState.Queued:
                _queuedSwitches--;
                if (ticket.Waiter is { } waiter && waiter.Node.List is not null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Cancellation.Unregister();
                    waiter.Completion.TrySetCanceled();
                }
                break;
            case SwitchState.Holding:
                _switch = null;
                break;
            default:
                return false;
        }

        ticket.State = SwitchState.Done;
        GrantNextSwitch();
        return true;
    }

    // Called with the lock held
    private void GrantNextSwitch()
    {
        if (_switch is not null || _runs > 0 || _waiters.First is not { } first)
        {
            return;
        }

        var waiter = first.Value;
        _waiters.RemoveFirst();
        // Not Dispose, which would wait for a cancellation callback running now, which waits for the lock
        waiter.Cancellation.Unregister();
        _queuedSwitches--;
        _switch = waiter.Ticket;
        waiter.Ticket.State = SwitchState.Holding;
        waiter.Completion.TrySetResult();
    }

    internal enum SwitchState
    {
        Queued,
        Holding,
        Done,
    }

    internal sealed class Waiter
    {
        public Waiter(SwitchTicket ticket)
        {
            Ticket = ticket;
            Node = new LinkedListNode<Waiter>(this);
        }

        public SwitchTicket Ticket { get; }
        public LinkedListNode<Waiter> Node { get; }

        // Continuations run elsewhere, so a switch doesn't go on under the lock or in the run that let go of the gate
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
    }

    private sealed class RunShare(VpnGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.LeaveRun();
            }
        }
    }

    /// <summary>
    /// A switch's place in the gate's queue, from <see cref="QueueSwitch"/>. Disposing it lets go of the gate, or gives up
    /// the place, and may be done more than once.
    /// </summary>
    public sealed class SwitchTicket : IDisposable
    {
        private readonly VpnGate _gate;

        internal SwitchTicket(VpnGate gate)
        {
            _gate = gate;
        }

        internal SwitchState State { get; set; } = SwitchState.Queued;

        internal Waiter? Waiter { get; set; }

        /// <summary>
        /// Waits until the switch has the gate to itself: every run that held a share has let go, and every switch that
        /// waited before it is done. Call it once. The switch still holds its place if it's cancelled.
        /// </summary>
        public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitForSwitchAsync(this, cancellationToken);

        public void Dispose() => _gate.EndSwitch(this);
    }
}
