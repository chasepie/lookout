using System.Threading.RateLimiting;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Polls the database for runs that are still <see cref="WatchRunStatus.Created"/> and executes them
/// one at a time, oldest first, by asking the Runner to run the watch's script.
/// A run of a watch with <see cref="CreateWatchDto.UseVpn"/> holds <see cref="VpnServiceKeys.Gate"/>, so it waits for a
/// VPN region switch in progress, staying <see cref="WatchRunStatus.Created"/> meanwhile.
/// </summary>
public partial class WatchRunWorker(
    IServiceScopeFactory scopeFactory,
    UpdatesNotifier notifier,
    [FromKeyedServices(VpnServiceKeys.Gate)] ConcurrencyLimiter vpnGate,
    ILogger<WatchRunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

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
                    .SetProperty(wr => wr.Error, "Interrupted because the Backend restarted."),
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
        using var vpnLease = watchRun.Watch.UseVpn
            ? await vpnGate.AcquireVpnLeaseAsync(stoppingToken)
            : null;

        // Claim the run with a conditional update, so it runs once even if another worker saw it too
        var startTime = DateTime.UtcNow;
        var claimed = await dbContext.WatchRuns
            .Where(wr => wr.Id == watchRun.Id && wr.Status == WatchRunStatus.Created)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(wr => wr.Status, WatchRunStatus.Running)
                    .SetProperty(wr => wr.StartTime, startTime),
                stoppingToken);
        if (claimed == 0)
        {
            return true;
        }

        // Mirror the claim on the tracked entity, so the notification and final save match the database
        watchRun.Status = WatchRunStatus.Running;
        watchRun.StartTime = startTime;
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

        watchRun.Status = WatchRunStatus.Done;
        watchRun.EndTime = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.RunUpdatedAsync(watchRun);
        return true;
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
        }

        if (screenshotProblem is null && result.Screenshot is { } keptScreenshot)
        {
            watchRun.Screenshot = keptScreenshot;
            watchRun.ScreenshotMediaType = ScreenshotImage.MediaTypeOf(keptScreenshot);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting watch run {WatchRunId} for watch {WatchId}")]
    private partial void LogRunStarted(int watchRunId, int watchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process pending watch runs")]
    private partial void LogPollFailed(Exception exception);
}
