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
/// <see cref="Vpn"/> holds them up, which stay <see cref="WatchRunStatus.Created"/> meanwhile: for a region switch, or
/// while the VPN is in a region their watch avoids.
/// </summary>
/// <remarks>
/// A VPN run holds a <see cref="VpnRun"/> from before it's claimed until it ends, and records the region it gives.
/// When a run's known failure asks to avoid its region, the region is saved as one its watch avoids, with a retry of the
/// watch, in the same save as the finished run; the retry's own start then waits for the VPN to leave the region.
/// </remarks>
public partial class WatchRunWorker(
    IServiceScopeFactory scopeFactory,
    UpdatesNotifier notifier,
    Vpn vpn,
    WatchRunLimit limit,
    ILogger<WatchRunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many retries in a row a watch gets at most. The last one still makes its region avoided, but isn't retried.
    /// </summary>
    public const int MaxRetriesInARow = 3;

    // Runs in progress, waited for when the worker stops. Guarded by itself
    private readonly HashSet<Task> _inProgress = [];

    // Runs in progress, each taking one of the limit's slots
    private int _running;

    // Released when a run ends or the VPN lets runs start again, so a run that can start now does without waiting for
    // the next poll
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        vpn.RunsMayStart += Wake;
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
            vpn.RunsMayStart -= Wake;

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
        var passedOver = new PassedOver();
        while (Volatile.Read(ref _running) < limit.MaxConcurrentRuns
            && await TryStartNextRunAsync(passedOver, stoppingToken))
        {
        }
    }

    /// <summary>
    /// The runs one pass over the pending runs has found can't start now.
    /// </summary>
    private sealed class PassedOver
    {
        /// <summary>Whether no VPN run can start now.</summary>
        public bool VpnHeldUp { get; set; }

        /// <summary>Watches whose next run waits for the VPN to leave a region they avoid.</summary>
        public List<int> WatchIds { get; } = [];
    }

    /// <summary>
    /// Claims the oldest run that can start now and starts executing it in the background.
    /// </summary>
    /// <returns><see langword="false"/> if no run can start now.</returns>
    private async Task<bool> TryStartNextRunAsync(PassedOver passedOver, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        var vpnHeldUp = passedOver.VpnHeldUp |= vpn.HoldsUpRuns;
        var passedOverWatchIds = passedOver.WatchIds;
        var next = await dbContext.WatchRuns
            .Where(wr => wr.Status == WatchRunStatus.Created)
            // The watch's oldest pending run, once its run in progress, if any, is done
            .Where(wr => !dbContext.WatchRuns.Any(other =>
                other.WatchId == wr.WatchId
                && (other.Status == WatchRunStatus.Running
                    || (other.Status == WatchRunStatus.Created && other.Id < wr.Id))))
            .Where(wr => !vpnHeldUp || !wr.Watch.UseVpn)
            .Where(wr => !passedOverWatchIds.Contains(wr.WatchId))
            .OrderBy(wr => wr.Id)
            .Select(wr => new { wr.Id, wr.WatchId, wr.Watch.UseVpn })
            .FirstOrDefaultAsync(stoppingToken);
        if (next is null)
        {
            return false;
        }

        // Taken before claiming, so a run held up by the VPN doesn't show as running
        VpnRun? vpnRun = null;
        string? cantStart = null;
        if (next.UseVpn)
        {
            var start = await vpn.TryStartRunAsync(next.WatchId, stoppingToken);
            switch (start.Outcome)
            {
                case VpnRunStartOutcome.Started:
                    vpnRun = start.Run;
                    break;
                case VpnRunStartOutcome.HeldUp:
                    // Look again, passing over VPN runs
                    passedOver.VpnHeldUp = true;
                    return true;
                case VpnRunStartOutcome.InAvoidedRegion:
                    // Look again, passing over the watch; other watches may still run in the region
                    passedOver.WatchIds.Add(next.WatchId);
                    return true;
                case VpnRunStartOutcome.NoRegionLeft:
                    cantStart = NoRegionLeftError;
                    break;
            }
        }

        var claimed = false;
        try
        {
            claimed = await ClaimAsync(dbContext, next.Id, vpnRun?.Region, stoppingToken);
        }
        finally
        {
            if (!claimed)
            {
                vpnRun?.Dispose();
            }
        }

        if (claimed)
        {
            Interlocked.Increment(ref _running);
            Track(Task.Run(() => RunAsync(next.Id, vpnRun, cantStart, stoppingToken), CancellationToken.None));
        }

        return true;
    }

    /// <summary>
    /// Why a run of a watch that avoids every region the VPN may switch to ends without running its script.
    /// </summary>
    public const string NoRegionLeftError =
        "The run couldn't start: the watch avoids every VPN region it may run in. Clear one of its avoided regions, "
        + "or stop excluding one, for its runs to start again.";

    /// <summary>
    /// Marks a pending run as running, in <paramref name="region"/>, its <see cref="VpnRun.Region"/> if it uses the VPN.
    /// </summary>
    /// <returns><see langword="false"/> if it was no longer pending.</returns>
    private static async Task<bool> ClaimAsync(
        LookoutDbContext dbContext, int runId, string? region, CancellationToken stoppingToken)
    {
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
    /// Executes a claimed run, then lets go of its <see cref="VpnRun"/>, if it has one, and of its slot.
    /// </summary>
    /// <param name="cantStart">Why the run ends as a crash without running its script, if it does.</param>
    private async Task RunAsync(int runId, VpnRun? vpnRun, string? cantStart, CancellationToken stoppingToken)
    {
        try
        {
            using (vpnRun)
            {
                await ExecuteRunAsync(runId, vpnRun, cantStart, stoppingToken);
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
    /// Keeps how a running run ended, works out its event and notifies of it, and, if its known failure asked to avoid
    /// its region, makes the region avoided and queues a retry.
    /// </summary>
    /// <param name="vpnRun">
    /// The run's lease, if it uses the VPN, as read when it was claimed, rather than the watch's setting now, which the
    /// Owner may have changed since: the run's region goes by it, so how it runs must too.
    /// </param>
    /// <param name="cantStart">Why the run ends as a crash without running its script, if it does.</param>
    private async Task ExecuteRunAsync(
        int runId, VpnRun? vpnRun, string? cantStart, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var watchRun = await dbContext.WatchRuns
            .Include(wr => wr.Watch)
            .SingleAsync(wr => wr.Id == runId, stoppingToken);

        if (cantStart is not null)
        {
            watchRun.Error = cantStart;
        }
        else
        {
            LogRunStarted(watchRun.Id, watchRun.WatchId);
            await notifier.RunUpdatedAsync(watchRun);
            try
            {
                var scriptName = WatchScriptsDirectory.GetStoredFileName(watchRun.Watch)
                    ?? throw new InvalidOperationException("The watch has no script.");
                var runner = scope.ServiceProvider.GetRequiredService<RunnerHttpClient>();
                var result = await runner.RunWatchScriptAsync(
                    scriptName: scriptName,
                    useVpn: vpnRun is not null,
                    screenshots: watchRun.Watch.GetScreenshotSettings(),
                    cancellationToken: stoppingToken);
                KeepResult(watchRun, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Includes the HttpClient timeout, which surfaces as a TaskCanceledException
                watchRun.Error = ex.Message;
            }
        }

        // Saved with the run, so the watch's next run can't start in the region before it's avoided
        var avoidsRegion = watchRun.AskedToAvoidRegion && vpnRun?.Region is not null;
        var retry = avoidsRegion ? await AvoidRegionAsync(dbContext, watchRun, vpnRun!.Region!, stoppingToken) : null;
        await FinishRunAsync(scope, dbContext, watchRun, stoppingToken);
        if (retry is not null)
        {
            await notifier.RunUpdatedAsync(retry);
        }

        if (avoidsRegion)
        {
            await notifier.WatchChangedAsync(watchRun.WatchId);
        }
    }

    /// <summary>
    /// Adds <paramref name="region"/> to the regions <paramref name="watchRun"/>'s watch avoids, unless it does already,
    /// and a retry of the watch, unless it has a later run already or the run was already the last retry in a row. Both
    /// are saved with the run.
    /// </summary>
    /// <returns>The retry, if there is one.</returns>
    private async Task<WatchRun?> AvoidRegionAsync(
        LookoutDbContext dbContext, WatchRun watchRun, string region, CancellationToken stoppingToken)
    {
        var avoided = await dbContext.AvoidedRegions
            .Where(a => a.WatchId == watchRun.WatchId)
            .Select(a => a.Region)
            .ToListAsync(stoppingToken);
        if (!avoided.Contains(region, StringComparer.OrdinalIgnoreCase))
        {
            dbContext.AvoidedRegions.Add(new AvoidedRegion
            {
                WatchId = watchRun.WatchId,
                Region = region,
                CreatedTime = DateTime.UtcNow,
                Run = watchRun,
            });
        }

        // A run queued since, such as a scheduled one, tries the watch again anyway
        if (await dbContext.WatchRuns.AnyAsync(
            wr => wr.WatchId == watchRun.WatchId && wr.Id > watchRun.Id, stoppingToken))
        {
            LogRunAlreadyQueued(watchRun.WatchId);
            return null;
        }

        // Each retry names the run it retries, so the ones in a row lead back through them
        var retriesInARow = 0;
        for (var retried = watchRun.RetryOfRunId; retried is { } id && retriesInARow < MaxRetriesInARow; retriesInARow++)
        {
            retried = await dbContext.WatchRuns
                .Where(wr => wr.Id == id)
                .Select(wr => wr.RetryOfRunId)
                .SingleOrDefaultAsync(stoppingToken);
        }

        if (retriesInARow >= MaxRetriesInARow)
        {
            LogRetriesUsedUp(watchRun.WatchId, MaxRetriesInARow);
            return null;
        }

        var retry = new WatchRun
        {
            WatchId = watchRun.WatchId,
            CreatedTime = DateTime.UtcNow,
            Status = WatchRunStatus.Created,
            RetryOfRunId = watchRun.Id,
        };
        dbContext.WatchRuns.Add(retry);
        return retry;
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

    private void Track(Task task)
    {
        lock (_inProgress)
        {
            _inProgress.RemoveWhere(t => t.IsCompleted);
            _inProgress.Add(task);
        }
    }

    /// <summary>
    /// Waits for every run in progress, including runs started meanwhile.
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
            watchRun.AskedToAvoidRegion = result.AvoidRegion;
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
        Message = "Watch {WatchId} isn't retried after avoiding a region: it was retried {Count} times in a row")]
    private partial void LogRetriesUsedUp(int watchId, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watch {WatchId} isn't retried after avoiding a region: it has a later run already")]
    private partial void LogRunAlreadyQueued(int watchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process pending watch runs")]
    private partial void LogPollFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute watch run {WatchRunId}")]
    private partial void LogRunFailed(Exception exception, int watchRunId);
}
