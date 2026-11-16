using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// The one scheduler of watch runs and VPN region switches. It polls the database for runs that are still
/// <see cref="WatchRunStatus.Created"/> and executes them by asking the Runner to run the watch's script, up to
/// <see cref="WatchRunLimit.MaxConcurrentRuns"/> at once, and makes the switches <see cref="Vpn"/> plans. Whenever fewer
/// runs are running, the oldest run that can start now starts. It passes over runs of a watch that already has one
/// running, so a watch never has two at once and its runs go in order, and runs of watches with
/// <see cref="CreateWatchDto.UseVpn"/> that can't start yet, which stay <see cref="WatchRunStatus.Created"/> meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// A switch restarts the tunnel, which would cut off a VPN run, so each turn of the loop goes: plan the switch that's
/// due, if none is planned or in progress; make the planned one, in the background, once no VPN run is in progress; then
/// start runs. While a switch is planned or in progress no VPN run starts, so a switch waits only for the runs already in
/// progress, and runs without the VPN keep starting. Only the loop changes the region, so a VPN run records the region
/// it read as it started. The loop alone reads and writes the planned switch and the switch in progress; the counts of
/// runs in progress are also lowered by the runs as they end.
/// </para>
/// <para>
/// A VPN run waits for the Backend's first switch, so it knows its region. In a region its watch avoids, it waits while
/// the VPN switches away from it for the watch, or ends as a crash if the watch has no region left, and other watches
/// keep running in the region meanwhile. When a run's known failure asks to avoid its region, the region is saved as
/// one its watch avoids, with a retry of the watch, in the same save as the finished run.
/// </para>
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

    /// <summary>
    /// Why a run of a watch that avoids every region the VPN may switch to ends without running its script.
    /// </summary>
    public const string NoRegionLeftError =
        "The run couldn't start: the watch avoids every VPN region it may run in. Clear one of its avoided regions, "
        + "or stop excluding one, for its runs to start again.";

    // Runs in progress, and the switch in progress, waited for when the worker stops. Guarded by itself
    private readonly HashSet<Task> _inProgress = [];

    // Runs in progress, each taking one of the limit's slots, and how many of them use the VPN
    private int _running;
    private int _vpnRunning;

    // The switch to make once no VPN run is in progress
    private VpnSwitch? _plannedSwitch;

    private Task _switch = Task.CompletedTask;

    // Released when a run or switch ends, so what waited for it starts without waiting for the next poll
    private readonly SemaphoreSlim _wake = new(0, 1);

    private bool SwitchPlannedOrInProgress => _plannedSwitch is not null || !_switch.IsCompleted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

                    await PlanSwitchAsync(stoppingToken);
                    StartPlannedSwitch(stoppingToken);
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
    /// Plans the switch that's due, if no switch is planned or in progress.
    /// </summary>
    private async Task PlanSwitchAsync(CancellationToken stoppingToken)
    {
        if (SwitchPlannedOrInProgress)
        {
            return;
        }

        try
        {
            _plannedSwitch = await vpn.PlanDueSwitchAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Only logged, so runs still start, and the check is made again in a minute
            LogPlanFailed(ex);
        }
    }

    /// <summary>
    /// Makes the planned switch in the background, once no VPN run is in progress.
    /// </summary>
    private void StartPlannedSwitch(CancellationToken stoppingToken)
    {
        if (_plannedSwitch is not { } planned || !_switch.IsCompleted || Volatile.Read(ref _vpnRunning) > 0)
        {
            return;
        }

        _plannedSwitch = null;
        _switch = Task.Run(() => SwitchAsync(planned, stoppingToken), CancellationToken.None);
        Track(_switch);
    }

    private async Task SwitchAsync(VpnSwitch planned, CancellationToken stoppingToken)
    {
        try
        {
            await vpn.SwitchAsync(planned, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogSwitchFailed(ex);
        }
        finally
        {
            Wake();
        }
    }

    /// <summary>
    /// Starts pending runs, oldest first, until every slot is taken or no other run can start now.
    /// </summary>
    private async Task StartRunsAsync(CancellationToken stoppingToken)
    {
        if (Volatile.Read(ref _running) >= limit.MaxConcurrentRuns)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var pending = await dbContext.WatchRuns
            .Where(wr => wr.Status == WatchRunStatus.Created)
            // The watch's oldest pending run, once its run in progress, if any, is done
            .Where(wr => !dbContext.WatchRuns.Any(other =>
                other.WatchId == wr.WatchId
                && (other.Status == WatchRunStatus.Running
                    || (other.Status == WatchRunStatus.Created && other.Id < wr.Id))))
            .OrderBy(wr => wr.Id)
            .Select(wr => new { wr.Id, wr.WatchId, wr.Watch.UseVpn })
            .ToListAsync(stoppingToken);

        // Read once, for the VPN runs that start this turn: no switch is in progress while they do, and only the loop
        // starts one
        string? region = null;
        var regionRead = false;
        foreach (var run in pending)
        {
            if (Volatile.Read(ref _running) >= limit.MaxConcurrentRuns)
            {
                return;
            }

            if (!run.UseVpn)
            {
                await StartRunAsync(dbContext, run.Id, useVpn: false, region: null, stoppingToken);
                continue;
            }

            if (SwitchPlannedOrInProgress)
            {
                continue;
            }

            if (!vpn.IsEnabled)
            {
                await StartRunAsync(dbContext, run.Id, useVpn: true, region: null, stoppingToken);
                continue;
            }

            if (!regionRead)
            {
                region = await vpn.GetCurrentRegionAsync(stoppingToken);
                regionRead = true;
            }

            if (region is null)
            {
                // Waits for the first switch, which the check of gluetun plans
                continue;
            }

            var avoided = await dbContext.AvoidedRegions
                .Where(a => a.WatchId == run.WatchId)
                .Select(a => a.Region)
                .ToListAsync(stoppingToken);
            if (!avoided.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                await StartRunAsync(dbContext, run.Id, useVpn: true, region, stoppingToken);
                continue;
            }

            var plan = await vpn.PlanSwitchForWatchAsync(run.WatchId, avoided, stoppingToken);
            if (plan.NoRegionLeft)
            {
                await EndWithNoRegionLeftAsync(dbContext, run.Id, stoppingToken);
            }
            else if (plan.Switch is not null)
            {
                // From now on no VPN run starts, so the switch waits only for those in progress. The next turn makes it
                // if none is
                _plannedSwitch = plan.Switch;
                Wake();
            }
        }
    }

    /// <summary>
    /// Claims a pending run and starts executing it in the background, taking a slot.
    /// </summary>
    /// <param name="region">The region the VPN is in, if the run uses it and the VPN is set up.</param>
    private async Task StartRunAsync(
        LookoutDbContext dbContext, int runId, bool useVpn, string? region, CancellationToken stoppingToken)
    {
        if (!await ClaimAsync(dbContext, runId, region, stoppingToken))
        {
            return;
        }

        // Counted before the next turn of the loop, which mustn't start a switch while the run is in progress
        Interlocked.Increment(ref _running);
        if (useVpn)
        {
            Interlocked.Increment(ref _vpnRunning);
        }

        Track(Task.Run(() => RunAsync(runId, useVpn, region, stoppingToken), CancellationToken.None));
    }

    /// <summary>
    /// Ends a pending run as a crash without running its script, or taking a slot, since its watch avoids every region
    /// the VPN may switch to for it. It goes through <see cref="FinishRunAsync"/>, as any run does, so a streak of them
    /// is notified.
    /// </summary>
    private async Task EndWithNoRegionLeftAsync(LookoutDbContext dbContext, int runId, CancellationToken stoppingToken)
    {
        if (!await ClaimAsync(dbContext, runId, null, stoppingToken))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var runDbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var watchRun = await runDbContext.WatchRuns
                .Include(wr => wr.Watch)
                .SingleAsync(wr => wr.Id == runId, stoppingToken);
            watchRun.Error = NoRegionLeftError;
            await FinishRunAsync(scope, runDbContext, watchRun, stoppingToken);
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
    }

    /// <summary>
    /// Marks a pending run as running, in <paramref name="region"/>.
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
    /// Executes a claimed run, then lets go of its slot.
    /// </summary>
    private async Task RunAsync(int runId, bool useVpn, string? region, CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteRunAsync(runId, useVpn, region, stoppingToken);
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
            if (useVpn)
            {
                Interlocked.Decrement(ref _vpnRunning);
            }

            Interlocked.Decrement(ref _running);
            Wake();
        }
    }

    /// <summary>
    /// Has the loop take its next turn now, rather than at the next poll.
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
    /// <param name="useVpn">
    /// Whether the run uses the VPN, as read when it was claimed, rather than the watch's setting now, which the Owner
    /// may have changed since: the run's region goes by it, so how it runs must too.
    /// </param>
    /// <param name="region">The region the run is in, if it uses the VPN and the VPN is set up.</param>
    private async Task ExecuteRunAsync(int runId, bool useVpn, string? region, CancellationToken stoppingToken)
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

        // Saved with the run, so the watch's next run can't start in the region before it's avoided
        var avoidsRegion = watchRun.AskedToAvoidRegion && region is not null;
        var retry = avoidsRegion
            ? await AvoidRegionAsync(dbContext, watchRun, region!, stoppingToken)
            : null;

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
        LookoutDbContext dbContext,
        WatchRun watchRun,
        string region,
        CancellationToken stoppingToken)
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
    /// Waits for every run and switch in progress, including runs started meanwhile.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check whether a VPN switch is due")]
    private partial void LogPlanFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to make a VPN switch")]
    private partial void LogSwitchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute watch run {WatchRunId}")]
    private partial void LogRunFailed(Exception exception, int watchRunId);
}
