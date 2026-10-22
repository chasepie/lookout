using ChasePie.Lookout.Backend.Events;

namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>
/// Rotates the VPN to a new exit IP every 10–15 minutes, or early after a Known failure, and at startup if it hasn't
/// connected within <see cref="StartupWait"/> (ADR-0004). A VPN Run holds a
/// <see cref="VpnRunLease"/> while it uses the VPN. No VPN Run starts during a Rotation, which waits for those in
/// progress to finish before it rotates. Direct Runs carry on throughout. Pushes the VPN's status through
/// <see cref="WatchEvents"/> as each Rotation starts and ends, and checks it every 30 seconds in between, pushing it
/// when it has changed.
/// </summary>
public sealed partial class VpnRotation(IVpnRotator vpn, WatchEvents events, TimeProvider timeProvider, ILogger<VpnRotation> logger)
    : BackgroundService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a Rotation waits for VPN Runs in progress: the longest Run timeout, by when the runner has stopped them.
    /// </summary>
    public static readonly TimeSpan MaxDrain = TimeSpan.FromSeconds(Model.Watch.MaxTimeoutSeconds);

    /// <summary>
    /// How long an attempt at a Rotation waits for a new exit IP before the next attempt. gluetun connects to a live
    /// server in a few seconds, and takes about 35 seconds to give up on a dead one by itself.
    /// </summary>
    public static readonly TimeSpan AttemptWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a Rotation keeps making attempts before it gives up, which lets VPN Runs start again. They're then
    /// refused unless the VPN connects. A time rather than a count of attempts, as each can take minutes when gluetun
    /// doesn't answer.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after startup a VPN that hasn't connected gets a Rotation, as gluetun may be stuck on a dead server,
    /// which it takes about 35 seconds to leave by itself.
    /// </summary>
    public static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(15);

    /// <summary>How soon after a Rotation a Known failure can bring on another.</summary>
    public static readonly TimeSpan EarlyRotationGap = TimeSpan.FromMinutes(3);

    /// <summary>How often the VPN's status is checked between Rotations, for the app to show changes such as a drop.</summary>
    public static readonly TimeSpan StatusWatchInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StatusCheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private int _vpnRuns;
    /// <summary>Completes once no VPN Run is in progress, while a Rotation waits for that.</summary>
    private TaskCompletionSource? _drained;
    /// <summary>Completes when the Rotation in progress, if any, ends.</summary>
    private TaskCompletionSource? _rotating;
    /// <summary>How many Rotations have started, so a request for an early one can tell if one started since.</summary>
    private long _rotationsStarted;
    private DateTimeOffset? _lastRotationEndedAt;
    private bool _earlyRotationRequested;
    /// <summary>Completes when an early Rotation is requested, waking the timer.</summary>
    private TaskCompletionSource _earlyRotationWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Taken to push the VPN's status, so pushes go out one at a time and in order.</summary>
    private readonly SemaphoreSlim _pushing = new(1, 1);
    /// <summary>The status the app last had pushed, or read at startup, compared without its error's wording.</summary>
    private (bool Enabled, bool Connected, bool Rotating, string? Region, string? ExitIp)? _lastPushed;
    private long _pushes;

    /// <summary>
    /// Starts a VPN Run, which a Rotation then waits for until the lease is disposed, or nothing during a Rotation.
    /// </summary>
    public VpnRunLease? TryStartVpnRun()
    {
        lock (_lock)
        {
            if (_rotating is not null)
            {
                return null;
            }
            _vpnRuns++;
            return new VpnRunLease(this, _rotationsStarted);
        }
    }

    /// <summary>The VPN's status, including whether it's rotating.</summary>
    public async Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await vpn.GetStatusAsync(cancellationToken);
        lock (_lock)
        {
            return _rotating is null ? status : status.WhileRotating();
        }
    }

    /// <summary>Waits for the Rotation in progress, if any, to end.</summary>
    public Task WhenNotRotatingAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _rotating?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Brings the next Rotation forward, though not to within <see cref="EarlyRotationGap"/> of the last one. A request
    /// from a VPN Run that started before the latest Rotation began is ignored, as that Rotation changes its exit IP.
    /// </summary>
    internal void RequestEarlyRotation(long rotationsStartedBefore)
    {
        lock (_lock)
        {
            if (rotationsStartedBefore != _rotationsStarted)
            {
                return;
            }
            _earlyRotationRequested = true;
            _earlyRotationWake.TrySetResult();
            _earlyRotationWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void FinishVpnRun()
    {
        lock (_lock)
        {
            if (--_vpnRuns == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RotateOnTimeAsync(stoppingToken), WatchStatusAsync(stoppingToken));

    private async Task RotateOnTimeAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The first timed Rotation is the check at startup, which only rotates a VPN that hasn't connected. With no
            // exit IP before it, any exit IP then completes it.
            var timedAt = timeProvider.GetUtcNow() + StartupWait;
            var atStartup = true;
            while (true)
            {
                if (!await SleepUntilDueAsync(timedAt, stoppingToken))
                {
                    continue;
                }
                try
                {
                    bool early;
                    lock (_lock)
                    {
                        early = _earlyRotationRequested;
                    }
                    await RotateAsync(stoppingToken, onlyIfDisconnected: atStartup && !early);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    LogRotationFailed(logger, error);
                }
                timedAt = timeProvider.GetUtcNow() + Interval();
                atStartup = false;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout is stopping.
        }
    }

    /// <summary>
    /// Checks the VPN's status every <see cref="StatusWatchInterval"/>, pushing it when it has changed since the last
    /// push. The first check only notes it, as the app reads the status when it opens. A Rotation pushes for itself.
    /// </summary>
    private async Task WatchStatusAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StatusWatchInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                long pushesBefore;
                lock (_lock)
                {
                    if (_rotating is not null)
                    {
                        continue;
                    }
                    pushesBefore = _pushes;
                }
                var status = await vpn.GetStatusAsync(stoppingToken);
                // A push since the read began, such as a Rotation's, is newer than this read.
                await PushAsync(status, onlyIfChanged: true, unlessPushedSince: pushesBefore);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout is stopping.
        }
    }

    /// <summary>
    /// Pushes <paramref name="status"/> to the app. A check of the status pushes it only if it has changed, and if
    /// nothing else has been pushed since the check began.
    /// </summary>
    private async Task PushAsync(VpnStatus status, bool onlyIfChanged = false, long? unlessPushedSince = null)
    {
        await _pushing.WaitAsync(CancellationToken.None);
        try
        {
            var shown = (status.Enabled, status.Connected, status.Rotating, status.Region, status.ExitIp);
            lock (_lock)
            {
                if (unlessPushedSince is { } pushes && (pushes != _pushes || _rotating is not null))
                {
                    return;
                }
                var first = _lastPushed is null;
                var unchanged = _lastPushed == shown;
                _lastPushed = shown;
                if (onlyIfChanged && (first || unchanged))
                {
                    return;
                }
                _pushes++;
            }
            await events.VpnStatusChangedAsync(status);
        }
        finally
        {
            _pushing.Release();
        }
    }

    /// <summary>
    /// Waits until the next Rotation is due, at <paramref name="timedAt"/> or earlier if one is requested. Returns
    /// whether it's due, or false if a request woke it to work out when.
    /// </summary>
    private async Task<bool> SleepUntilDueAsync(DateTimeOffset timedAt, CancellationToken stoppingToken)
    {
        DateTimeOffset dueAt;
        Task woken;
        lock (_lock)
        {
            var earliest = _lastRotationEndedAt + EarlyRotationGap ?? DateTimeOffset.MinValue;
            dueAt = _earlyRotationRequested && earliest < timedAt ? earliest : timedAt;
            woken = _earlyRotationWake.Task;
        }
        var wait = dueAt - timeProvider.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return true;
        }
        using var sleeping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var slept = Task.Delay(wait, timeProvider, sleeping.Token);
        var first = await Task.WhenAny(slept, woken);
        await sleeping.CancelAsync();
        stoppingToken.ThrowIfCancellationRequested();
        return first == slept;
    }

    private async Task RotateAsync(CancellationToken stoppingToken, bool onlyIfDisconnected = false)
    {
        // Read first, so a VPN that isn't there to rotate doesn't hold up VPN Runs, which are refused anyway. A VPN Run
        // that starts meanwhile is waited for like any other.
        var before = await vpn.GetStatusAsync(stoppingToken);
        if (before is not { Enabled: true, Misconfigured: false } || (onlyIfDisconnected && before.Connected))
        {
            return;
        }
        TaskCompletionSource rotating;
        lock (_lock)
        {
            rotating = _rotating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _rotationsStarted++;
            _earlyRotationRequested = false;
        }
        VpnStatus? after = null;
        try
        {
            await PushAsync(before.WhileRotating());
            await DrainAsync(stoppingToken);
            after = await RotateUntilNewExitAsync(before.ExitIp, stoppingToken);
        }
        finally
        {
            lock (_lock)
            {
                _drained = null;
                _rotating = null;
                _lastRotationEndedAt = timeProvider.GetUtcNow();
            }
            rotating.SetResult();
            // Only stopping leaves no status after a Rotation, and then there's no one left to tell.
            if (after is not null)
            {
                await PushAsync(after);
            }
        }
    }

    /// <summary>Waits for VPN Runs in progress to finish, but no longer than <see cref="MaxDrain"/>.</summary>
    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        Task drained;
        lock (_lock)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_vpnRuns == 0)
            {
                return;
            }
            drained = _drained.Task;
        }
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timedOut = Task.Delay(MaxDrain, timeProvider, giveUp.Token);
        await Task.WhenAny(drained, timedOut);
        await giveUp.CancelAsync();
        stoppingToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Makes attempts until the VPN connects with an exit IP other than <paramref name="notedIp"/>, the one before the
    /// first attempt, as gluetun may pick the same exit again or a dead server (ADR-0004). Returns the VPN's status
    /// then, or when it gives up.
    /// </summary>
    private async Task<VpnStatus> RotateUntilNewExitAsync(string? notedIp, CancellationToken stoppingToken)
    {
        var giveUpAt = timeProvider.GetUtcNow() + GiveUpAfter;
        var status = VpnStatus.Disconnected();
        var attempt = 0;
        while (timeProvider.GetUtcNow() < giveUpAt)
        {
            attempt++;
            try
            {
                await vpn.RotateAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // gluetun may not have stopped the VPN, so the exit IP is unchanged and the next attempt follows.
                LogAttemptFailed(logger, error, attempt);
            }

            var attemptEnds = timeProvider.GetUtcNow() + AttemptWait;
            while (true)
            {
                status = await vpn.GetStatusAsync(stoppingToken);
                if (status.Connected && status.ExitIp != notedIp)
                {
                    return status;
                }
                if (status.Misconfigured)
                {
                    LogGaveUp(logger, attempt, status.Error);
                    return status;
                }
                var remaining = attemptEnds - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                await Task.Delay(StatusCheckInterval < remaining ? StatusCheckInterval : remaining, timeProvider, stoppingToken);
            }
        }
        LogGaveUp(logger, attempt, status.Error);
        return status;
    }

    /// <summary>A random time between Rotations, so they don't fall on a pattern a site could spot.</summary>
    private static TimeSpan Interval() =>
        MinInterval + (MaxInterval - MinInterval) * Random.Shared.NextDouble();

    public override void Dispose()
    {
        base.Dispose();
        _pushing.Dispose();
    }

    [LoggerMessage(LogLevel.Warning, "Attempt {Attempt} at rotating the VPN failed.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception error, int attempt);

    [LoggerMessage(LogLevel.Warning, "Gave up rotating the VPN after {Attempts} attempts without a new exit IP. {Error}")]
    private static partial void LogGaveUp(ILogger logger, int attempts, string? error);

    [LoggerMessage(LogLevel.Error, "Could not rotate the VPN.")]
    private static partial void LogRotationFailed(ILogger logger, Exception error);
}

/// <summary>A VPN Run in progress, which a Rotation waits for until it's disposed.</summary>
public sealed class VpnRunLease : IDisposable
{
    private readonly VpnRotation _rotation;
    private readonly long _rotationsStartedBefore;
    private int _disposed;

    internal VpnRunLease(VpnRotation rotation, long rotationsStartedBefore)
    {
        _rotation = rotation;
        _rotationsStartedBefore = rotationsStartedBefore;
    }

    /// <summary>Asks for an early Rotation, as the VPN Run's Script recognised its exit IP was blocked.</summary>
    public void RequestEarlyRotation() => _rotation.RequestEarlyRotation(_rotationsStartedBefore);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _rotation.FinishVpnRun();
        }
    }
}
