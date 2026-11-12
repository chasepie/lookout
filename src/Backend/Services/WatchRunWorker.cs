using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Polls the database for runs that are still <see cref="WatchRunStatus.Created"/> and executes them by asking the Runner
/// to run the watch's script, up to <see cref="WatchRunLimit.MaxConcurrentRuns"/> at once. Whenever fewer are running,
/// the oldest run that can start now starts. It passes over runs of a watch that already has one running, so a watch
/// never has two at once and its runs go in order, and runs of watches with <see cref="CreateWatchDto.UseVpn"/> while
/// <see cref="VpnGate"/> holds them up for a region switch, which stay <see cref="WatchRunStatus.Created"/> meanwhile.
/// </summary>
/// <remarks>
/// A VPN run holds a share of <see cref="VpnGate"/>, which any number of them hold at once. When a run's known failure
/// asks for a failover, the run queues the failover in the gate before it lets go of its share, so no VPN run starts in
/// the region that failed; VPN runs already in progress there finish as usual. The failover, then the retry it queues,
/// carry on in the background without taking a slot.
/// </remarks>
public partial class WatchRunWorker(
    IServiceScopeFactory scopeFactory,
    UpdatesNotifier notifier,
    VpnGate vpnGate,
    VpnConfiguration vpnConfiguration,
    VpnRotationService vpnRotation,
    WatchRunLimit limit,
    ILogger<WatchRunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many retries in a row a watch gets at most. The last one still fails over, but isn't retried.
    /// </summary>
    public const int MaxRetriesInARow = 3;

    // Runs and failovers in progress, waited for when the worker stops. Guarded by itself
    private readonly HashSet<Task> _inProgress = [];

    // Runs in progress, each taking one of the limit's slots
    private int _running;

    // Released when a run ends or the VPN gate lets runs in again, so a run that can start now does without waiting for
    // the next poll
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        vpnGate.RunsMayEnter += Wake;
        try
        {
            var interruptedRunsEnded = false;
            while (true)
            {
                try
                {
                    // Before any run starts, since it ends every run left running, and tried again until it works, since
                    // a run left running keeps its watch's later runs from starting
                    if (!interruptedRunsEnded)
                    {
                        await FailInterruptedRunsAsync(stoppingToken);
                        interruptedRunsEnded = true;
                    }

                    await StartRunsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogPollFailed(ex);
                }

                await _wake.WaitAsync(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            vpnGate.RunsMayEnter -= Wake;

            // Cancelled with stoppingToken; a run cut off stays running, for the next start to mark as interrupted
            await WhenInProgressDoneAsync();
        }
    }

    /// <summary>
    /// Marks runs left <see cref="WatchRunStatus.Running"/> by a previous process as done with an error.
    /// Runs only progress inside this worker, so any found before it starts were cut off.
    /// </summary>
    private async Task FailInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        var now = DateTime.UtcNow;
        await dbContext.WatchRuns
            .Where(wr => wr.Status == WatchRunStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(wr => wr.Status, WatchRunStatus.Done)
                    .SetProperty(wr => wr.EndTime, now)
                    .SetProperty(wr => wr.Error, "Interrupted because the Backend restarted.")
                    .SetProperty(wr => wr.Interrupted, true),
                cancellationToken);
    }

    /// <summary>
    /// Starts pending runs until every slot is taken or no run can start now.
    /// </summary>
    private async Task StartRunsAsync(CancellationToken stoppingToken)
    {
        while (Volatile.Read(ref _running) < limit.MaxConcurrentRuns
            && await TryStartNextRunAsync(stoppingToken))
        {
        }
    }

    /// <summary>
    /// Claims the oldest run that can start now and starts executing it in the background.
    /// </summary>
    /// <returns><see langword="false"/> if no run can start now.</returns>
    private async Task<bool> TryStartNextRunAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        var vpnHeldUp = vpnGate.HoldsUpRuns;
        var next = await dbContext.WatchRuns
            .Where(wr => wr.Status == WatchRunStatus.Created)
            // The watch's oldest pending run, once its run in progress, if any, is done
            .Where(wr => !dbContext.WatchRuns.Any(other =>
                other.WatchId == wr.WatchId
                && (other.Status == WatchRunStatus.Running
                    || (other.Status == WatchRunStatus.Created && other.Id < wr.Id))))
            .Where(wr => !vpnHeldUp || !wr.Watch.UseVpn)
            .OrderBy(wr => wr.Id)
            .Select(wr => new { wr.Id, wr.Watch.UseVpn })
            .FirstOrDefaultAsync(stoppingToken);
        if (next is null)
        {
            return false;
        }

        // Taken before claiming, so a run held up by a switch doesn't show as running
        var vpnShare = next.UseVpn ? vpnGate.TryEnterRun() : null;
        if (next.UseVpn && vpnShare is null)
        {
            // A switch was queued since; look again, passing over VPN runs
            return true;
        }

        var claimed = false;
        try
        {
            claimed = await ClaimAsync(dbContext, next.Id, next.UseVpn, stoppingToken);
        }
        finally
        {
            if (!claimed)
            {
                vpnShare?.Dispose();
            }
        }

        if (claimed)
        {
            Interlocked.Increment(ref _running);
            Track(Task.Run(() => RunAsync(next.Id, next.UseVpn, vpnShare, stoppingToken), CancellationToken.None));
        }

        return true;
    }

    /// <summary>
    /// Marks a pending run as running, with the VPN's region if it uses the VPN.
    /// </summary>
    /// <returns><see langword="false"/> if it was no longer pending.</returns>
    private async Task<bool> ClaimAsync(
        LookoutDbContext dbContext, int runId, bool useVpn, CancellationToken stoppingToken)
    {
        // Read while holding a share of the gate, so no switch changes it during the run
        var region = useVpn && vpnConfiguration.IsEnabled
            ? await dbContext.VpnSettings
                .Where(s => s.Id == VpnSettings.SingletonId)
                .Select(s => s.CurrentRegion)
                .SingleAsync(stoppingToken)
            : null;

        // A conditional update, so the run runs once even if it changed since it was read
        var startTime = DateTime.UtcNow;
        var claimed = await dbContext.WatchRuns
            .Where(wr => wr.Id == runId && wr.Status == WatchRunStatus.Created)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(wr => wr.Status, WatchRunStatus.Running)
                    .SetProperty(wr => wr.StartTime, startTime)
                    .SetProperty(wr => wr.Region, region),
                stoppingToken);
        return claimed > 0;
    }

    /// <summary>
    /// Executes a claimed run, then lets go of its share of the VPN gate, if it has one, and of its slot.
    /// </summary>
    private async Task RunAsync(int runId, bool useVpn, IDisposable? vpnShare, CancellationToken stoppingToken)
    {
        try
        {
            using (vpnShare)
            {
                await ExecuteRunAsync(runId, useVpn, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Left running, for the next start to mark as interrupted
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, runId);
            await EndBrokenRunAsync(runId, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Wake();
        }
    }

    /// <summary>
    /// Has the loop look for runs to start now, rather than at the next poll.
    /// </summary>
    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled
        }
    }

    /// <summary>
    /// Keeps how a running run ended, works out its event and notifies of it, and queues its failover if it asked for
    /// one.
    /// </summary>
    /// <param name="useVpn">
    /// Whether the run uses the VPN, as read when it was claimed, rather than the watch's setting now, which the Owner may
    /// have changed since: the run's share of the gate and its region go by it, so how it runs must too.
    /// </param>
    private async Task ExecuteRunAsync(int runId, bool useVpn, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var watchRun = await dbContext.WatchRuns
            .Include(wr => wr.Watch)
            .SingleAsync(wr => wr.Id == runId, stoppingToken);
        LogRunStarted(watchRun.Id, watchRun.WatchId);
        await notifier.RunUpdatedAsync(watchRun);

        try
        {
            var scriptName = WatchScriptsDirectory.GetStoredFileName(watchRun.Watch)
                ?? throw new InvalidOperationException("The watch has no script.");
            var runner = scope.ServiceProvider.GetRequiredService<RunnerHttpClient>();
            var result = await runner.RunWatchScriptAsync(
                scriptName: scriptName,
                useVpn: useVpn,
                screenshots: watchRun.Watch.GetScreenshotSettings(),
                cancellationToken: stoppingToken);
            KeepResult(watchRun, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Includes the HttpClient timeout, which surfaces as a TaskCanceledException
            watchRun.Error = ex.Message;
        }

        // Queued before the run is saved as done and lets go of its share of the gate, so no VPN run starts in the
        // region that failed, neither this watch's next run nor another's
        var failover = watchRun.AskedForFailover && useVpn && vpnConfiguration.IsEnabled
            ? vpnGate.QueueSwitch()
            : null;
        try
        {
            await FinishRunAsync(scope, dbContext, watchRun, stoppingToken);
            if (failover is not null)
            {
                var ticket = failover;
                failover = null;
                // Only what the failover needs, so the run, with its screenshot, isn't kept while it waits
                var asking = new RunAskingForFailover(
                    watchRun.Id, watchRun.WatchId, watchRun.Region, watchRun.RetryOfRunId);
                // In the background, so the run's slot is free for another run meanwhile
                Track(Task.Run(() => FailOverAsync(asking, ticket, stoppingToken), CancellationToken.None));
            }
        }
        finally
        {
            failover?.Dispose();
        }
    }

    /// <summary>
    /// Works out the event of a run that has ended, saves it as done, and sends its notifications.
    /// </summary>
    private async Task FinishRunAsync(
        IServiceScope scope,
        LookoutDbContext dbContext,
        WatchRun watchRun,
        CancellationToken stoppingToken)
    {
        // Worked out before the run is saved as done, so the event is kept with it for later runs to go by
        var notifications = scope.ServiceProvider.GetRequiredService<RunNotifications>();
        RunEvent? runEvent = null;
        try
        {
            runEvent = await notifications.DetectAsync(watchRun, stoppingToken);
            watchRun.Event = runEvent?.Event;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // The run is kept whatever happens to its event
            LogNotifyFailed(ex, watchRun.Id);
        }

        watchRun.Status = WatchRunStatus.Done;
        watchRun.EndTime = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.RunUpdatedAsync(watchRun);

        if (runEvent is not null)
        {
            try
            {
                await notifications.NotifyAsync(watchRun, runEvent, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The run is kept whatever happens to its notifications
                LogNotifyFailed(ex, watchRun.Id);
            }
        }
    }

    /// <summary>
    /// Ends a run that failed for a reason of the Backend's own, such as its database, rather than leave it running,
    /// which would keep its watch's later runs from starting until the Backend restarts. It's interrupted, as when the
    /// Backend restarts, since it says nothing about the watch: as a crash, it would make the watch's next success a
    /// recovery, and keep its next crash from being notified.
    /// </summary>
    private async Task EndBrokenRunAsync(int runId, Exception exception)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var now = DateTime.UtcNow;
            var ended = await dbContext.WatchRuns
                .Where(wr => wr.Id == runId && wr.Status == WatchRunStatus.Running)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(wr => wr.Status, WatchRunStatus.Done)
                        .SetProperty(wr => wr.EndTime, now)
                        .SetProperty(wr => wr.Error, $"Lookout couldn't finish the run: {exception.Message}")
                        .SetProperty(wr => wr.Interrupted, true),
                    CancellationToken.None);

            // So clients don't keep showing it as running
            if (ended > 0
                && await dbContext.WatchRuns.AsNoTracking().SingleOrDefaultAsync(wr => wr.Id == runId) is { } watchRun)
            {
                await notifier.RunUpdatedAsync(watchRun);
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, runId);
        }
    }

    /// <summary>
    /// The run whose known failure asked for a failover: what the failover and its retry need of it.
    /// </summary>
    private sealed record RunAskingForFailover(int Id, int WatchId, string? Region, int? RetryOfRunId);

    /// <summary>
    /// Fails the VPN over from the region <paramref name="watchRun"/> ran in, then queues a retry of its watch, unless
    /// the failover failed or found no region, or the run was already the last retry in a row.
    /// </summary>
    /// <param name="ticket">The failover's place in the gate's queue, disposed when it's done.</param>
    private async Task FailOverAsync(
        RunAskingForFailover watchRun, VpnGate.SwitchTicket ticket, CancellationToken stoppingToken)
    {
        try
        {
            using (ticket)
            {
                await FailOverAndRetryAsync(watchRun, ticket, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailoverFailed(ex, watchRun.Id);
        }
    }

    private async Task FailOverAndRetryAsync(
        RunAskingForFailover watchRun, VpnGate.SwitchTicket ticket, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var earlierRunsQuery = dbContext.WatchRuns
            .AsNoTracking()
            .Where(wr => wr.WatchId == watchRun.WatchId && wr.Id < watchRun.Id && wr.Status == WatchRunStatus.Done);
        // A success starts the known-failure streak over, so only the runs since the watch's previous one are read, as
        // for the run's event. They also hold every retry in a row before this run, since each retried a known failure.
        var previousSuccessId = await earlierRunsQuery
            .Where(WatchEvents.IsSuccessQuery)
            .MaxAsync(wr => (int?)wr.Id, stoppingToken) ?? 0;
        var sinceSuccess = await earlierRunsQuery
            .Where(wr => wr.Id > previousSuccessId)
            .Select(wr => new { wr.Id, wr.AskedForFailover, wr.Region, wr.RetryOfRunId })
            .ToListAsync(stoppingToken);

        // Only known failures ask, so crashes add no region to the list
        var failedRegions = sinceSuccess
            .Where(wr => wr.AskedForFailover && wr.Region is not null)
            .Select(wr => wr.Region!)
            .ToList();

        var outcome = await vpnRotation.FailOverAsync(watchRun.Region, failedRegions, ticket, stoppingToken);
        if (outcome is not (FailoverOutcome.Switched or FailoverOutcome.AlreadyLeft))
        {
            return;
        }

        // Each retry names the run it retries, so the ones in a row lead back through them
        var retriesInARow = 0;
        var byId = sinceSuccess.ToDictionary(wr => wr.Id, wr => wr.RetryOfRunId);
        for (var retried = watchRun.RetryOfRunId; retried is { } id; retried = byId.GetValueOrDefault(id))
        {
            retriesInARow++;
        }

        if (retriesInARow >= MaxRetriesInARow)
        {
            LogRetriesUsedUp(watchRun.WatchId, MaxRetriesInARow);
            return;
        }

        // A run queued since, such as a scheduled one, runs in the new region anyway. Whatever its status: the switch let
        // VPN runs in again once the tunnel was up, so it may have started, or even finished, by now
        if (await dbContext.WatchRuns.AnyAsync(
            wr => wr.WatchId == watchRun.WatchId && wr.Id > watchRun.Id, stoppingToken))
        {
            LogRunAlreadyQueued(watchRun.WatchId);
            return;
        }

        var retry = new WatchRun
        {
            WatchId = watchRun.WatchId,
            CreatedTime = DateTime.UtcNow,
            Status = WatchRunStatus.Created,
            RetryOfRunId = watchRun.Id,
        };
        dbContext.WatchRuns.Add(retry);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.RunUpdatedAsync(retry);
    }

    private void Track(Task task)
    {
        lock (_inProgress)
        {
            _inProgress.RemoveWhere(t => t.IsCompleted);
            _inProgress.Add(task);
        }
    }

    /// <summary>
    /// Waits for every run and failover in progress, including failovers that runs start meanwhile.
    /// </summary>
    private async Task WhenInProgressDoneAsync()
    {
        while (true)
        {
            Task[] inProgress;
            lock (_inProgress)
            {
                _inProgress.RemoveWhere(t => t.IsCompleted);
                if (_inProgress.Count == 0)
                {
                    return;
                }

                inProgress = [.. _inProgress];
            }

            // Each handles its own failures
            await Task.WhenAll(inProgress).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Keeps how the script ended on the run, with its screenshot. A screenshot that can't be kept makes the run a crash,
    /// unless it crashed already, since the script, which can send anything, returned it.
    /// </summary>
    private static void KeepResult(WatchRun watchRun, WatchRunResult result)
    {
        var screenshotProblem = result.Screenshot is { } screenshot
            ? ScreenshotImage.ProblemWith(screenshot)
            : null;
        if (result.Crash is { } crash)
        {
            // Blank, as when the script threw '', it would read as a run with nothing to report
            watchRun.Error = string.IsNullOrWhiteSpace(crash.Error)
                ? "The script crashed without saying why."
                : crash.Error;
        }
        else if (screenshotProblem is not null)
        {
            watchRun.Error = screenshotProblem;
        }
        else if (result.Success)
        {
            watchRun.Content = result.Content;
        }
        else
        {
            watchRun.FailureReason = string.IsNullOrWhiteSpace(result.FailureReason)
                ? "The script reported a known failure without giving a reason."
                : result.FailureReason;
            watchRun.AskedForFailover = result.Failover;
        }

        if (screenshotProblem is null && result.Screenshot is { } keptScreenshot)
        {
            watchRun.Screenshot = keptScreenshot;
            watchRun.ScreenshotMediaType = ScreenshotImage.MediaTypeOf(keptScreenshot);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting watch run {WatchRunId} for watch {WatchId}")]
    private partial void LogRunStarted(int watchRunId, int watchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to work out or send the notifications of watch run {WatchRunId}")]
    private partial void LogNotifyFailed(Exception exception, int watchRunId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch {WatchId} isn't retried after its failover: it was retried {Count} times in a row")]
    private partial void LogRetriesUsedUp(int watchId, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch {WatchId} isn't retried after its failover: it has a later run already")]
    private partial void LogRunAlreadyQueued(int watchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process pending watch runs")]
    private partial void LogPollFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute watch run {WatchRunId}")]
    private partial void LogRunFailed(Exception exception, int watchRunId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to fail the VPN over after watch run {WatchRunId}")]
    private partial void LogFailoverFailed(Exception exception, int watchRunId);
}
