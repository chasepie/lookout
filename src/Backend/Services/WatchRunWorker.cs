using System.Threading.RateLimiting;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Polls the database for runs that are still <see cref="WatchRunStatus.Created"/> and executes them
/// one at a time, oldest first, by asking the Runner to run the watch's script.
/// A run of a watch with <see cref="CreateWatchDto.UseVpn"/> holds <see cref="VpnServiceKeys.Gate"/>, so it waits for a
/// VPN region switch in progress, staying <see cref="WatchRunStatus.Created"/> meanwhile. When a run's known failure
/// asks for a failover, the worker waits for it before the next run, then queues a retry.
/// </summary>
public partial class WatchRunWorker(
    IServiceScopeFactory scopeFactory,
    UpdatesNotifier notifier,
    [FromKeyedServices(VpnServiceKeys.Gate)] ConcurrencyLimiter vpnGate,
    VpnConfiguration vpnConfiguration,
    VpnRotationService vpnRotation,
    ILogger<WatchRunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many retries in a row a watch gets at most. The last one still fails over, but isn't retried.
    /// </summary>
    public const int MaxRetriesInARow = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await FailInterruptedRunsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPollFailed(ex);
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                while (await TryExecuteNextRunAsync(stoppingToken))
                {
                    // Drain everything pending before waiting for the next tick
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPollFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
    /// Claims and executes the oldest pending run.
    /// </summary>
    /// <returns><see langword="false"/> if there was nothing to run.</returns>
    private async Task<bool> TryExecuteNextRunAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        var watchRun = await dbContext.WatchRuns
            .Include(wr => wr.Watch)
            .Where(wr => wr.Status == WatchRunStatus.Created)
            .OrderBy(wr => wr.Id)
            .FirstOrDefaultAsync(stoppingToken);
        if (watchRun is null)
        {
            return false;
        }

        // Taken before claiming, so a run waiting for a switch doesn't show as running
        using (watchRun.Watch.UseVpn ? await vpnGate.AcquireVpnLeaseAsync(stoppingToken) : null)
        {
            if (!await ExecuteRunAsync(scope, dbContext, watchRun, stoppingToken))
            {
                return true;
            }
        }

        // Once the run has let go of the VPN, which the switch needs, and before the next run, so none runs in the
        // region that just failed
        if (watchRun.AskedForFailover
            && watchRun.Watch.UseVpn
            && vpnConfiguration.IsEnabled)
        {
            await FailOverAsync(dbContext, watchRun, stoppingToken);
        }

        return true;
    }

    /// <summary>
    /// Claims and executes a pending run, holding the VPN gate if it needs it, and keeps how it ended.
    /// </summary>
    /// <returns><see langword="false"/> if another worker claimed it first.</returns>
    private async Task<bool> ExecuteRunAsync(
        IServiceScope scope,
        LookoutDbContext dbContext,
        WatchRun watchRun,
        CancellationToken stoppingToken)
    {
        // Read while holding the gate, so no switch changes it during the run
        var region = watchRun.Watch.UseVpn && vpnConfiguration.IsEnabled
            ? await dbContext.VpnSettings
                .Where(s => s.Id == VpnSettings.SingletonId)
                .Select(s => s.CurrentRegion)
                .SingleAsync(stoppingToken)
            : null;

        // Claim the run with a conditional update, so it runs once even if another worker saw it too
        var startTime = DateTime.UtcNow;
        var claimed = await dbContext.WatchRuns
            .Where(wr => wr.Id == watchRun.Id && wr.Status == WatchRunStatus.Created)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(wr => wr.Status, WatchRunStatus.Running)
                    .SetProperty(wr => wr.StartTime, startTime)
                    .SetProperty(wr => wr.Region, region),
                stoppingToken);
        if (claimed == 0)
        {
            return false;
        }

        // Mirror the claim on the tracked entity, so the notification and final save match the database
        watchRun.Status = WatchRunStatus.Running;
        watchRun.StartTime = startTime;
        watchRun.Region = region;
        LogRunStarted(watchRun.Id, watchRun.WatchId);
        await notifier.RunUpdatedAsync(watchRun);

        try
        {
            var scriptName = WatchScriptsDirectory.GetStoredFileName(watchRun.Watch)
                ?? throw new InvalidOperationException("The watch has no script.");
            var runner = scope.ServiceProvider.GetRequiredService<RunnerHttpClient>();
            var result = await runner.RunWatchScriptAsync(
                scriptName: scriptName,
                useVpn: watchRun.Watch.UseVpn,
                screenshots: watchRun.Watch.GetScreenshotSettings(),
                cancellationToken: stoppingToken);
            KeepResult(watchRun, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Includes the HttpClient timeout, which surfaces as a TaskCanceledException
            watchRun.Error = ex.Message;
        }

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

        return true;
    }

    /// <summary>
    /// Fails the VPN over from the region <paramref name="watchRun"/> ran in, then queues a retry of its watch, unless
    /// the failover failed or found no region, or the run was already the last retry in a row.
    /// </summary>
    private async Task FailOverAsync(LookoutDbContext dbContext, WatchRun watchRun, CancellationToken stoppingToken)
    {
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

        var outcome = await vpnRotation.FailOverAsync(watchRun.Region, failedRegions, stoppingToken);
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

        var retry = new WatchRun(watchRun.Watch) { RetryOfRunId = watchRun.Id };
        dbContext.WatchRuns.Add(retry);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.RunUpdatedAsync(retry);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process pending watch runs")]
    private partial void LogPollFailed(Exception exception);
}
