namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Keeps runs that use the VPN and region switches apart: a switch restarts the tunnel, which would cut off a run using
/// it. Any number of VPN runs share the gate, and a switch needs it to itself. Only <see cref="Vpn"/> uses it: each
/// <see cref="VpnRun"/> holds a share, taken with <see cref="TryEnterRun"/>, and a switch holds it alone, taken with
/// <see cref="EnterSwitchAsync"/>, from asking gluetun to switch until the new tunnel is up and the switch has saved
/// how it went.
/// </summary>
/// <remarks>
/// Switches go first: once one waits, no new run gets a share until it's done, so runs can't keep a switch waiting for
/// good, and a switch waits only for the runs already in progress. Runs don't queue: one that can't enter is passed
/// over, and tried again later. <see cref="Vpn"/> makes one switch at a time, so the gate takes only one.
/// </remarks>
internal sealed class VpnGate
{
    private readonly Lock _lock = new();

    private int _runs;
    private SwitchHold? _switch;

    /// <summary>
    /// Whether a run can't enter now, since a switch is waiting or in progress.
    /// </summary>
    public bool HoldsUpRuns
    {
        get
        {
            lock (_lock)
            {
                return _switch is not null;
            }
        }
    }

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
    /// Raised when a switch is done or given up, so runs held up by it can enter now. Raised outside the gate's lock, on
    /// the thread that ended the switch; handlers must be quick and not throw.
    /// </summary>
    public event Action? RunsMayEnter;

    /// <summary>
    /// Takes a share for a run, unless a switch is waiting or in progress. Dispose it when the run ends.
    /// </summary>
    /// <returns>The share, or <see langword="null"/> if the run must wait.</returns>
    public IDisposable? TryEnterRun()
    {
        lock (_lock)
        {
            if (_switch is not null)
            {
                return null;
            }

            _runs++;
            return new RunShare(this);
        }
    }

    /// <summary>
    /// Keeps new runs out from now on, and waits until every run that holds a share has let go. Dispose what it returns
    /// when the switch is done. Cancelling it gives the switch up, letting runs in again.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another switch is waiting or in progress.</exception>
    public Task<IDisposable> EnterSwitchAsync(CancellationToken cancellationToken)
    {
        var hold = new SwitchHold(this);
        lock (_lock)
        {
            if (_switch is not null)
            {
                throw new InvalidOperationException("Another switch is waiting for the gate, or has it.");
            }

            _switch = hold;
            GrantSwitchIfFree();
        }

        // Outside the lock, since a token cancelled already runs the callback at once. Once granted, it does nothing
        hold.Cancellation = cancellationToken.Register(() =>
        {
            if (EndSwitch(hold, onlyIfWaiting: true))
            {
                hold.Granted.TrySetCanceled(cancellationToken);
            }
        });
        return hold.Granted.Task;
    }

    private void LeaveRun()
    {
        lock (_lock)
        {
            _runs--;
            GrantSwitchIfFree();
        }
    }

    // Called with the lock held
    private void GrantSwitchIfFree()
    {
        if (_switch is not { Holding: false } waiting || _runs > 0)
        {
            return;
        }

        waiting.Holding = true;
        waiting.Granted.TrySetResult(waiting);
    }

    /// <returns>Whether the switch was ended now, rather than already.</returns>
    private bool EndSwitch(SwitchHold hold, bool onlyIfWaiting = false)
    {
        lock (_lock)
        {
            if (_switch != hold || (onlyIfWaiting && hold.Holding))
            {
                return false;
            }

            _switch = null;
        }

        RunsMayEnter?.Invoke();
        return true;
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
    /// A switch's hold on the gate, from <see cref="EnterSwitchAsync"/>. Disposing it lets go, and may be done more
    /// than once.
    /// </summary>
    private sealed class SwitchHold(VpnGate gate) : IDisposable
    {
        // Continuations run elsewhere, so a switch doesn't go on under the lock or in the run that let go of the gate
        public TaskCompletionSource<IDisposable> Granted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Cancellation { get; set; }

        // Guarded by the gate's lock
        public bool Holding { get; set; }

        public void Dispose()
        {
            Cancellation.Unregister();
            gate.EndSwitch(this);
        }
    }
}
