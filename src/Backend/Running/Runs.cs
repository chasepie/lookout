using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Running;

/// <summary>
/// Admits, executes and records Runs, scheduled or on demand, and executes Test runs. Both take one of a limited number
/// of Run slots, first come, first served. A Watch is Running from when its Run is admitted, including while it waits
/// for a slot, until the Run is discarded, or recorded and its notifications sent; a Watch never has two Runs in this
/// process, though
/// <see cref="Watch.Revision"/> still rejects a stale Run recorded by another. A VPN Run gives its slot back while it
/// waits for the VPN to connect or a <see cref="VpnRotation">Rotation</see> to end, and goes to the front of the
/// queue to take one again. Sends each recorded Run's notifications, and announces each Run's start and finish, and any
/// change of the Watch's status, through <see cref="WatchEvents"/>. Lookout stopping cancels Runs, and waits for them
/// to finish.
/// </summary>
public sealed partial class Runs(
    int slotLimit,
    IServiceScopeFactory scopes,
    VpnRotation rotation,
    WatchEvents events,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<Runs> logger)
    : IHostedService
{
    private readonly CancellationToken _stopping = lifetime.ApplicationStopping;
    private readonly ConcurrentDictionary<Guid, Entry> _running = new();
    private readonly ConcurrentDictionary<Task, byte> _inProgress = new();

    private readonly Lock _slotsLock = new();
    private int _freeSlots = slotLimit;
    /// <summary>VPN Runs taking a slot back after waiting for the VPN, which come before those waiting for their first.</summary>
    private readonly LinkedList<TaskCompletionSource> _retakingSlot = new();
    private readonly LinkedList<TaskCompletionSource> _waitingForSlot = new();

    /// <summary>
    /// Admits a Run of the Watch, unless it already has one, and executes it in the background. The Watch is Running,
    /// and the Run queued for a slot, by the time this returns. A <paramref name="gate"/> is asked once the Run has a slot.
    /// </summary>
    public Admission Run(Guid watchId, RunGate? gate = null)
    {
        var entry = new Entry(gate is not null);
        var existing = _running.GetOrAdd(watchId, entry);
        if (existing != entry)
        {
            return new Refused(
                Reason: existing.Executing ? RunRefusal.Executing : RunRefusal.WaitingForSlot,
                Gated: existing.Gated);
        }

        // Queued now rather than in the background, so Runs admitted one after another take slots in that order.
        var slot = TakeSlotAsync(retaking: false, _stopping);
        var finished = RunAdmittedAsync(watchId, entry, gate, slot);
        _inProgress.TryAdd(finished, 0);
        _ = finished.ContinueWith(done =>
        {
            // Observed here, in case whoever admitted the Run doesn't wait for it.
            _ = done.Exception;
            _inProgress.TryRemove(done, out _);
        }, TaskScheduler.Default);
        return new Admitted(finished);
    }

    /// <summary>
    /// Executes <paramref name="source"/> as a Test run with the Watch's settings, or returns null if the Watch doesn't
    /// exist. It waits for a slot, but isn't one of the Watch's Runs: it doesn't make the Watch Running, may execute
    /// alongside its Run, and its result is neither evaluated against the Baseline nor recorded. A VPN Watch's Test
    /// run is refused at once, rather than waiting for the VPN, as the user is waiting for the result.
    /// </summary>
    public async Task<TestRun?> TestAsync(Guid watchId, string source, string fileKind, CancellationToken cancellationToken)
    {
        RunnerResult result;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var watch = await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Watches.AsNoTracking()
                .Where(watch => watch.Id == watchId)
                .Select(watch => new
                {
                    watch.NotificationUrl,
                    watch.TimeoutSeconds,
                    watch.UsesVpn
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (watch is null)
            {
                return null;
            }

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
            await TakeSlotAsync(retaking: false, stopping.Token);
            VpnRunLease? vpnRun = null;
            try
            {
                // Checked once the slot is taken, which can be minutes later. A Rotation waits for it like a VPN Run, so
                // its browser isn't left with a connection through the old exit.
                VpnStatus? vpnStatus = null;
                if (watch.UsesVpn)
                {
                    (vpnRun, vpnStatus) = await rotation.TryStartVpnRunAsync(stopping.Token);
                    if (vpnStatus is null)
                    {
                        return RefusedTestRun("The VPN is rotating to a new exit IP, so this Test run was not started. Try again in a minute.");
                    }
                }

                if (vpnStatus?.GetNotStartedReason("Test run") is { } refusal)
                {
                    return RefusedTestRun(refusal);
                }

                result = await scope.ServiceProvider.GetRequiredService<IRunnerClient>().ExecuteAsync(new RunnerRequest(
                    Source: source,
                    FileKind: fileKind,
                    Context: new ScriptContext(watch.NotificationUrl),
                    TimeoutMs: watch.TimeoutSeconds * 1000,
                    AlwaysScreenshot: true,
                    Proxy: vpnStatus?.Proxy), stopping.Token);
            }
            finally
            {
                vpnRun?.Dispose();
                ReleaseSlot();
            }
        }

        // Shows the outcome a Run would record for the same result.
        var (outcome, errorMessage) = RunEvaluation.RecordedOutcome(
            result.Outcome,
            result.Content,
            result.ErrorMessage);
        return new TestRun(
            Outcome: outcome,
            Content: result.Content,
            Details: result.Details,
            ErrorMessage: errorMessage,
            DurationMs: result.DurationMs,
            Screenshot: result.Screenshot,
            ConsoleOutput: result.ConsoleOutput);
    }

    /// <summary>
    /// Whether the Watch is Running: from when its Run is admitted until it's discarded, or recorded and its
    /// notifications sent.
    /// </summary>
    public bool IsRunning(Guid watchId) => _running.ContainsKey(watchId);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Waits for the Runs in progress, which Lookout stopping has cancelled, to finish, recording any the runner had
    /// already answered.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var inProgress = _inProgress.Keys.ToList();
        try
        {
            await Task.WhenAll(inProgress).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stops waiting after its shutdown timeout, and so does this, rather than failing the shutdown.
            LogStoppedWithRunsInProgress(logger, inProgress.Count(run => !run.IsCompleted));
        }
        catch (Exception)
        {
            // Each Run's ending reached whoever admitted it.
        }
    }

    private static TestRun RefusedTestRun(string errorMessage) => new(
        Outcome: RunOutcome.Errored,
        Content: null,
        Details: null,
        ErrorMessage: errorMessage,
        DurationMs: 0,
        Screenshot: null,
        ConsoleOutput: null);

    private async Task<RunExecution> RunAdmittedAsync(Guid watchId, Entry entry, RunGate? gate, Task slot)
    {
        // Off the caller, so neither admitting nor announcing holds up the scheduler or a request. Every admitted Run is
        // announced once as it starts and once as it finishes, recorded or not.
        await Task.Yield();
        await events.RunStartedAsync(watchId);
        var hasSlot = false;
        try
        {
            await slot;
            // Held from here on: a VPN Run waiting for the VPN takes its slot back before it carries on or stops.
            hasSlot = true;
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            if (gate is not null && !await gate(_stopping))
            {
                return new RunExecution(RunExecutionStatus.NotClaimed);
            }
            entry.Executing = true;

            var (execution, statusBefore, toSend) = await ExecuteAsync(scope.ServiceProvider, database, watchId);
            if (execution.Run is { } recorded)
            {
                // Sent while the Watch is still Running, so another of its Runs can't notify before this one has. Like
                // the save, notifications of a finished Run still go out if shutdown started; senders time out by
                // themselves.
                await scope.ServiceProvider
                    .GetRequiredService<NotificationDispatcher>()
                    .NotifyAsync(watchId, toSend, CancellationToken.None);
                await FinishAsync(watchId, entry, recorded);
                // Read back, as the tracked Watch misses the user pausing or resuming it while the Run executed. A Run
                // doesn't change a paused Watch's status, and pausing announced it.
                if (await WatchStatuses.ReadAsync(database.Watches, watchId, CancellationToken.None) is { } statusAfter
                    && statusAfter != statusBefore && statusAfter != WatchStatus.Paused)
                {
                    await events.StatusChangedAsync(watchId, statusAfter);
                }
            }
            return execution;
        }
        finally
        {
            // A Run that wasn't recorded finishes here.
            await FinishAsync(watchId, entry, null);
            if (hasSlot)
            {
                ReleaseSlot();
            }
        }
    }

    /// <summary>
    /// Ends the Watch's Running, unless it already ended, then announces the Run's end, so the app reads the Watch as no
    /// longer Running when it reloads on the announcement.
    /// </summary>
    private Task FinishAsync(Guid watchId, Entry entry, Run? run) =>
        _running.TryRemove(new KeyValuePair<Guid, Entry>(watchId, entry))
            ? events.RunFinishedAsync(watchId, run)
            : Task.CompletedTask;

    /// <summary>
    /// Executes the Watch's current Script and records the Run, returning the Watch's status before it and the
    /// notifications the Run calls for once recorded. A VPN Run gives its slot back while it waits for the VPN.
    /// </summary>
    private async Task<(RunExecution Execution, WatchStatus? StatusBefore, IReadOnlyList<Notification> Notifications)> ExecuteAsync(
        IServiceProvider services, LookoutDbContext database, Guid watchId)
    {
        var cancellationToken = _stopping;
        var watch = await database.Watches
            .Include(watch => watch.CurrentScriptVersion)
            .SingleOrDefaultAsync(watch => watch.Id == watchId, cancellationToken);
        if (watch is null)
        {
            return (new RunExecution(RunExecutionStatus.WatchNotFound), null, []);
        }

        var script = watch.CurrentScriptVersion;
        if (script is null)
        {
            return (new RunExecution(RunExecutionStatus.NoScriptVersion), null, []);
        }

        var statusBefore = WatchStatuses.Of(watch);
        var startedAt = timeProvider.GetUtcNow();
        // Only a connected VPN has an exit and a proxy, so a refused Run records neither.
        (VpnStatus? vpnStatus, VpnRunLease? vpnRun) = watch.UsesVpn
            ? await StartVpnRunAsync()
            : (null, null);
        RunnerResult result;
        using (vpnRun)
        {
            if (vpnStatus?.GetNotStartedReason("VPN Run") is { } refusal)
            {
                result = new RunnerResult
                {
                    Outcome = RunOutcome.Errored,
                    ErrorMessage = refusal,
                    DurationMs = 0
                };
            }
            else
            {
                var runRequest = new RunnerRequest(
                    Source: script.Source,
                    FileKind: script.FileKind,
                    Context: new ScriptContext(watch.NotificationUrl),
                    TimeoutMs: watch.TimeoutSeconds * 1000,
                    Proxy: vpnStatus?.Proxy);
                result = await services.GetRequiredService<IRunnerClient>().ExecuteAsync(runRequest, cancellationToken);
                // A Known failure is often a site blocking the exit IP. The Run isn't retried; the next uses the new IP.
                if (result.Outcome == RunOutcome.KnownFailure)
                {
                    vpnRun?.RequestEarlyRotation();
                }
            }
        }

        var evaluation = RunEvaluation.Evaluate(watch, result.Outcome, result.Content, result.Details, result.ErrorMessage);
        var run = new Run
        {
            WatchId = watchId,
            ScriptVersionId = script.Id,
            Outcome = evaluation.Outcome,
            Content = result.Content,
            Details = result.Details,
            ErrorMessage = evaluation.ErrorMessage,
            DurationMs = result.DurationMs,
            StartedAt = startedAt,
            FinishedAt = timeProvider.GetUtcNow(),
            IsChange = evaluation.IsChange,
            VpnRegion = vpnStatus?.Region,
            VpnExitIp = vpnStatus?.ExitIp
        };
        watch.Baseline = evaluation.Baseline;
        watch.FailureStreak = evaluation.FailureStreak;
        watch.FailureStreakNotified = evaluation.FailureStreakNotified;
        watch.Revision++;
        database.Runs.Add(run);
        var debugOutput = DebugOutputOf(run, result);
        if (debugOutput is not null)
        {
            database.RunDebugOutputs.Add(debugOutput);
        }

        try
        {
            // The Run has finished, so record it even if shutdown started while the runner was replying.
            await database.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException exception)
        {
            // A deleted Watch fails either the Run's foreign key or the Watch's concurrency check, whichever EF sends first.
            var current = await database.Watches.AsNoTracking()
                .Where(item => item.Id == watchId)
                .Select(item => new { item.CurrentScriptVersionId })
                .SingleOrDefaultAsync(CancellationToken.None);
            if (current is null)
            {
                return (new RunExecution(RunExecutionStatus.Deleted), statusBefore, []);
            }
            if (exception is DbUpdateConcurrencyException)
            {
                var status = current.CurrentScriptVersionId == script.Id
                    ? RunExecutionStatus.Superseded
                    : RunExecutionStatus.ScriptReplaced;
                return (new RunExecution(status), statusBefore, []);
            }
            throw;
        }

        if (debugOutput is not null)
        {
            await TrimDebugOutput(database, watchId);
        }

        return (new RunExecution(RunExecutionStatus.Recorded, run), statusBefore, evaluation.Notifications);
    }

    /// <summary>
    /// Starts a VPN Run once the VPN is connected and no Rotation is in progress, returning the VPN's status and the
    /// lease the Run holds while it uses the VPN. A VPN Run that can't go ahead has no lease, and the status says why:
    /// the VPN didn't connect in time, or isn't configured, or can't connect until its setup is fixed, which aren't
    /// waited for.
    /// </summary>
    private async Task<(VpnStatus Status, VpnRunLease? VpnRun)> StartVpnRunAsync()
    {
        var cancellationToken = _stopping;
        var deadline = timeProvider.GetUtcNow() + VpnRotation.ConnectWait;
        VpnStatus? status = null;
        while (true)
        {
            var (vpnRun, started) = await rotation.TryStartVpnRunAsync(cancellationToken);
            if (vpnRun is not null)
            {
                return (started!, vpnRun);
            }
            if (started is not null)
            {
                status = started;
                if (status is not { Enabled: true, Misconfigured: false } || timeProvider.GetUtcNow() >= deadline)
                {
                    return (status, null);
                }
            }

            // Waiting mustn't hold a slot: VPN Runs waiting on a VPN that's down or rotating would stop every other Run.
            ReleaseSlot();
            try
            {
                if (await rotation.WaitForVpnRunAsync(deadline, status, cancellationToken) is { } refused)
                {
                    return (refused, null);
                }
            }
            finally
            {
                // Taken back even when stopping, as the Run releases it when it ends. Every other Run is then stopping
                // too, so one frees up soon.
                await TakeSlotAsync(retaking: true, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Drops the Watch's debug output beyond its most recent Runs'. Only a Run that kept debug output can take the Watch
    /// past its limit. The Run is already recorded, so a failure here is only logged, and the next such Run trims again.
    /// </summary>
    private async Task TrimDebugOutput(LookoutDbContext database, Guid watchId)
    {
        try
        {
            await database.RunDebugOutputs
                .Where(item => item.Run!.WatchId == watchId)
                .OrderByDescending(item => item.RunId)
                .Skip(RunDebugOutput.KeptPerWatch)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            LogTrimFailed(logger, error, watchId);
        }
    }

    /// <summary>What a Known failure or Errored Run left to debug it by, if anything; a Succeeded Run keeps none.</summary>
    private static RunDebugOutput? DebugOutputOf(Run run, RunnerResult result)
    {
        if (run.Outcome == RunOutcome.Succeeded)
        {
            return null;
        }

        var consoleOutput = string.IsNullOrEmpty(result.ConsoleOutput) ? null : result.ConsoleOutput;
        var screenshot = result.Screenshot is null ? null : DecodeScreenshot(result.Screenshot);
        return consoleOutput is null && screenshot is null
            ? null
            : new RunDebugOutput { Run = run, ConsoleOutput = consoleOutput, Screenshot = screenshot };
    }

    /// <summary>The runner's base64 screenshot, or nothing if it isn't valid base64, which shouldn't cost the Run.</summary>
    private static byte[]? DecodeScreenshot(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a Run slot, waiting behind those already waiting, though a Run <paramref name="retaking"/> its slot waits
    /// only behind others doing so. Queues at once, so the order of calls is the order slots are handed out.
    /// </summary>
    private Task TakeSlotAsync(bool retaking, CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_slotsLock)
        {
            if (_freeSlots > 0)
            {
                _freeSlots--;
                return Task.CompletedTask;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (retaking ? _retakingSlot : _waitingForSlot).AddLast(waiter);
        }
        var cancelled = cancellationToken.Register(() =>
        {
            lock (_slotsLock)
            {
                // Already handed a slot, which its Run releases.
                if (node.List is not { } queue)
                {
                    return;
                }
                queue.Remove(node);
            }
            waiter.TrySetCanceled(cancellationToken);
        });
        _ = waiter.Task.ContinueWith(_ => cancelled.Dispose(), TaskScheduler.Default);
        return waiter.Task;
    }

    /// <summary>Hands the slot to the first waiting for one, or frees it.</summary>
    private void ReleaseSlot()
    {
        lock (_slotsLock)
        {
            if ((_retakingSlot.First ?? _waitingForSlot.First) is { } next)
            {
                next.List!.Remove(next);
                next.Value.SetResult();
                return;
            }
            _freeSlots++;
        }
    }

    /// <summary>An admitted Run, while its Watch is Running.</summary>
    private sealed class Entry(bool gated)
    {
        public bool Gated { get; } = gated;
        /// <summary>Whether the Run has had a slot and its gate, if any, let it go ahead.</summary>
        public volatile bool Executing;
    }

    [LoggerMessage(LogLevel.Warning, "Lookout stopped before {Count} Runs in progress finished.")]
    private static partial void LogStoppedWithRunsInProgress(ILogger logger, int count);

    [LoggerMessage(LogLevel.Warning, "Could not drop older debug output of Watch {WatchId}.")]
    private static partial void LogTrimFailed(ILogger logger, Exception error, Guid watchId);
}
