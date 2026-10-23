using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend;

public enum RunExecutionStatus
{
    Recorded,
    WatchNotFound,
    NoScriptVersion,
    /// <summary>Another Run of the Watch was recorded while this one executed, so this one was discarded.</summary>
    Superseded,
    /// <summary>A new Script version was uploaded while this Run executed, so this Run of the old one was discarded.</summary>
    ScriptReplaced,
    /// <summary>The Watch was deleted while this Run executed, so there was nothing to record it against.</summary>
    Deleted
}

public sealed record RunExecution(RunExecutionStatus Status, Run? Run = null);

/// <summary>
/// Executes a Watch's current Script, records the Run and sends its notifications, for both scheduled and on-demand Runs.
/// Callers admit the Run first through <see cref="RunningWatches"/> and <see cref="RunSlots"/>; a VPN Run gives its slot
/// back while it waits for the VPN to connect or a <see cref="VpnRotation">Rotation</see> to end, and takes one again
/// before it returns.
/// Announces the Run's start and finish, and any change of the Watch's status, through <see cref="WatchEvents"/>.
/// </summary>
public sealed partial class RunExecutor(
    LookoutDbContext database,
    IRunnerClient runner,
    VpnRotation rotation,
    RunSlots runSlots,
    NotificationDispatcher notifications,
    WatchEvents events,
    TimeProvider timeProvider,
    ILogger<RunExecutor> logger)
{
    public async Task<RunExecution> ExecuteAsync(Guid watchId, CancellationToken cancellationToken)
    {
        var watch = await database.Watches
            .Include(watch => watch.CurrentScriptVersion)
            .SingleOrDefaultAsync(watch => watch.Id == watchId, cancellationToken);
        if (watch is null)
        {
            return new RunExecution(RunExecutionStatus.WatchNotFound);
        }

        var script = watch.CurrentScriptVersion;
        if (script is null)
        {
            return new RunExecution(RunExecutionStatus.NoScriptVersion);
        }

        var status = WatchSummary.StatusOf(watch);
        var startedAt = timeProvider.GetUtcNow();
        await events.RunStartedAsync(watchId, startedAt);
        RunExecution execution;
        IReadOnlyList<Notification> toSend;
        try
        {
            (execution, toSend) = await RunScriptAsync(watch, script, startedAt, cancellationToken);
        }
        catch
        {
            // A Run that started always finishes, so the app stops showing it as Running.
            await events.RunFinishedAsync(watchId, null);
            throw;
        }

        await events.RunFinishedAsync(watchId, execution.Run);
        // Read back, as the tracked Watch misses the user pausing or resuming it while the Run executed. A Run doesn't
        // change a paused Watch's status, and pausing announced it.
        if (execution.Run is not null
            && await WatchSummary.StatusAsync(database.Watches, watchId, CancellationToken.None) is { } newStatus
            && newStatus != status && newStatus != WatchStatus.Paused)
        {
            await events.StatusChangedAsync(watchId, newStatus);
        }

        // After the events, so the app isn't held up by sending. Like the save, notifications of a finished Run still go
        // out if shutdown started; senders time out by themselves.
        await notifications.NotifyAsync(watchId, toSend, CancellationToken.None);
        return execution;
    }

    /// <summary>Runs the Script and records the Run, returning the notifications it calls for once recorded.</summary>
    private async Task<(RunExecution Execution, IReadOnlyList<Notification> Notifications)> RunScriptAsync(Watch watch,
        ScriptVersion script, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var watchId = watch.Id;
        // Only a connected VPN has an exit and a proxy, so a refused Run records neither.
        (VpnStatus? vpnStatus, VpnRunLease? vpnRun) = watch.UsesVpn
            ? await StartVpnRunAsync(cancellationToken)
            : (null, null);
        RunnerResult result;
        using (vpnRun)
        {
            if (vpnStatus?.RefusalOf("VPN Run") is { } refusal)
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
                result = await runner.ExecuteAsync(runRequest, cancellationToken);
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
                return (new RunExecution(RunExecutionStatus.Deleted), []);
            }
            if (exception is DbUpdateConcurrencyException)
            {
                var status = current.CurrentScriptVersionId == script.Id
                    ? RunExecutionStatus.Superseded
                    : RunExecutionStatus.ScriptReplaced;
                return (new RunExecution(status), []);
            }
            throw;
        }

        if (debugOutput is not null)
        {
            await TrimDebugOutput(watchId);
        }

        return (new RunExecution(RunExecutionStatus.Recorded, run), evaluation.Notifications);
    }

    /// <summary>
    /// Drops the Watch's debug output beyond its most recent Runs'. Only a Run that kept debug output can take the Watch
    /// past its limit. The Run is already recorded, so a failure here is only logged, and the next such Run trims again.
    /// </summary>
    private async Task TrimDebugOutput(Guid watchId)
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

    [LoggerMessage(LogLevel.Warning, "Could not drop older debug output of Watch {WatchId}.")]
    private static partial void LogTrimFailed(ILogger logger, Exception error, Guid watchId);

    /// <summary>
    /// Starts a VPN Run once the VPN is connected and no Rotation is in progress, returning the VPN's status and the
    /// lease the Run holds while it uses the VPN. A VPN Run that can't go ahead has no lease, and the status says why:
    /// the VPN didn't connect in time, or isn't configured, or can't connect until its setup is fixed, which aren't
    /// waited for.
    /// </summary>
    private async Task<(VpnStatus Status, VpnRunLease? VpnRun)> StartVpnRunAsync(CancellationToken cancellationToken)
    {
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

            // The caller holds a Run slot, which waiting mustn't: VPN Runs waiting on a VPN that's down or rotating
            // would stop every other Run.
            runSlots.Release();
            try
            {
                if (await rotation.WaitForVpnRunAsync(deadline, status, cancellationToken) is { } refused)
                {
                    return (refused, null);
                }
            }
            finally
            {
                // The caller releases the slot once the Run is recorded, so it's taken back even when stopping. Every
                // other Run is then stopping too, so one frees up soon.
                await runSlots.WaitAsync(CancellationToken.None);
            }
        }
    }
}
