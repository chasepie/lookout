using System.Threading.Channels;
using System.Threading.RateLimiting;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Makes every VPN region switch: rotations when <see cref="VpnSettings.NextRotationTime"/> comes due,
/// switches asked for with <see cref="RequestSwitch"/>, failovers asked for with <see cref="FailOverAsync"/>, a first
/// switch when it has made none, so the region is always known, reapplying <see cref="VpnSettings.CurrentRegion"/>
/// when gluetun has lost it, which happens when gluetun restarts, and replacing it when
/// <see cref="VpnConfiguration.ServerRegions"/> no longer allows it.
/// </summary>
/// <remarks>
/// A switch holds <see cref="VpnServiceKeys.Gate"/> until the new tunnel is up, so it waits for a VPN run in progress and
/// VPN runs wait for it. Every switch to a new region restarts the rotation timer. A failed rotation is retried after
/// <see cref="RetryDelay"/>. Does nothing unless <see cref="VpnConfiguration.IsEnabled"/>.
/// </remarks>
public partial class VpnRotationService(
    IServiceScopeFactory scopeFactory,
    VpnConfiguration configuration,
    [FromKeyedServices(VpnServiceKeys.Gate)] ConcurrencyLimiter gate,
    VpnRegionCatalog catalog,
    UpdatesNotifier notifier,
    ILogger<VpnRotationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TunnelUpTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private enum SwitchKind
    {
        Rotation,
        Manual,

        /// <summary>
        /// Back to <see cref="VpnSettings.CurrentRegion"/>, which gluetun lost when it restarted.
        /// </summary>
        Reapply,

        /// <summary>
        /// Away from <see cref="VpnSettings.CurrentRegion"/>, which <see cref="VpnConfiguration.ServerRegions"/>
        /// no longer allows, to any region it does. Exclusions only apply to rotation, so they don't apply here.
        /// </summary>
        Replacement,

        /// <summary>
        /// Away from a region a run's known failure asked to leave, to one rotation may switch to that the watch hasn't
        /// failed in during its known-failure streak.
        /// </summary>
        Failover,

        /// <summary>
        /// To a region rotation may switch to, when the Backend hasn't switched yet. Until then gluetun picks a region
        /// of its own and doesn't say which, so runs couldn't record theirs or a failover avoid it.
        /// </summary>
        First,
    }

    // Requested regions, null for a random one. Only the latest matters; one made while another waits replaces it
    private readonly Channel<string?> _requests = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private volatile VpnSwitchProgress? _progress;

    // Failovers switch from the run worker, and the rest from this service's loop: one at a time, so each picks its
    // region from where the last one left the VPN, and only one shows its progress
    private readonly SemaphoreSlim _switching = new(1, 1);

    /// <summary>
    /// The switch in progress, or <see langword="null"/> if there's none.
    /// </summary>
    public VpnSwitchProgress? Progress => _progress;

    /// <summary>
    /// Queues a switch, made within a second unless another switch or a VPN run is in progress.
    /// Clients hear about it through <see cref="IUpdatesClient.VpnChanged"/>.
    /// </summary>
    /// <param name="region">A region from <see cref="VpnRegionCatalog"/>, or <see langword="null"/> for a random
    /// one. Excluded regions are allowed: exclusions only apply to rotation.</param>
    public void RequestSwitch(string? region)
    {
        _requests.Writer.TryWrite(region);
    }

    /// <summary>
    /// Fails the VPN over from <paramref name="failedRegion"/>, where a run's known failure asked for it, to a random
    /// region rotation may switch to that isn't one of <paramref name="failedRegions"/>, whether or not rotation is on.
    /// Waits for the gate, then for the new tunnel.
    /// </summary>
    /// <param name="failedRegion">The region the run ran in.</param>
    /// <param name="failedRegions">The regions the watch already failed over from during its known-failure streak.</param>
    public async Task<FailoverOutcome> FailOverAsync(
        string? failedRegion,
        IReadOnlyCollection<string> failedRegions,
        CancellationToken cancellationToken)
    {
        if (!configuration.IsEnabled)
        {
            return FailoverOutcome.Unavailable;
        }

        return await SwitchAsync(
            kind: SwitchKind.Failover,
            requestedRegion: null,
            cancellationToken: cancellationToken,
            failover: new FailoverRequest(failedRegion, failedRegions));
    }

    private sealed record FailoverRequest(string? FailedRegion, IReadOnlyCollection<string> FailedRegions)
    {
        public bool HasLeft(string? currentRegion) =>
            !string.Equals(currentRegion, FailedRegion, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.IsEnabled)
        {
            return;
        }

        var nextReconcileTime = DateTime.MinValue;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                if (_requests.Reader.TryRead(out var requestedRegion))
                {
                    await SwitchAsync(SwitchKind.Manual, requestedRegion, stoppingToken);
                }
                else if (await IsRotationDueAsync(stoppingToken))
                {
                    await SwitchAsync(SwitchKind.Rotation, null, stoppingToken);
                }
                else if (DateTime.UtcNow >= nextReconcileTime)
                {
                    nextReconcileTime = DateTime.UtcNow + ReconcileInterval;
                    await ReapplyIfLostAsync(stoppingToken);
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

    private async Task<bool> IsRotationDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = DateTime.UtcNow;
        return await dbContext.VpnSettings.AnyAsync(
            s => s.Id == VpnSettings.SingletonId
                && s.RotationEnabled
                && s.NextRotationTime <= now,
            cancellationToken);
    }

    /// <summary>
    /// Switches back to <see cref="VpnSettings.CurrentRegion"/> if gluetun isn't restricted to it.
    /// gluetun keeps settings changed through its API only until it restarts. If the region is no longer one of
    /// <see cref="VpnConfiguration.ServerRegions"/>, switches to a random one that is instead. If the Backend hasn't
    /// switched yet, makes its first switch, and tries again next time if that fails.
    /// </summary>
    private async Task ReapplyIfLostAsync(CancellationToken cancellationToken)
    {
        // The checks each use a scope of their own, so none is held through a switch, which can wait minutes
        if (await GetCurrentRegionAsync(cancellationToken) is not { } currentRegion)
        {
            LogFirstSwitch();
            await SwitchAsync(SwitchKind.First, null, cancellationToken);
            return;
        }

        if (!configuration.AllowsRegion(currentRegion))
        {
            // GLUETUN_SERVER_REGIONS changed since the switch to it, and reapplying it would undo the change
            LogRegionNotAllowed(currentRegion);
            await SwitchAsync(SwitchKind.Replacement, null, cancellationToken);
        }
        else if (!await IsGluetunRestrictedToAsync(currentRegion, cancellationToken))
        {
            LogReapplying(currentRegion);
            await SwitchAsync(SwitchKind.Reapply, currentRegion, cancellationToken);
        }
    }

    private async Task<string?> GetCurrentRegionAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        return await dbContext.VpnSettings
            .Where(s => s.Id == VpnSettings.SingletonId)
            .Select(s => s.CurrentRegion)
            .SingleAsync(cancellationToken);
    }

    private async Task<bool> IsGluetunRestrictedToAsync(string region, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        var gluetunSettings = await gluetun.GetVpnSettingsAsync(cancellationToken);
        return gluetunSettings.Provider.ServerSelection.Regions is [var restrictedTo]
            && string.Equals(restrictedTo, region, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the region, switches gluetun to it and waits for the tunnel, then records the outcome.
    /// Failures are recorded in <see cref="VpnSettings.LastError"/> rather than thrown.
    /// </summary>
    /// <param name="failover">What a <see cref="SwitchKind.Failover"/> fails over from.</param>
    private async Task<FailoverOutcome> SwitchAsync(
        SwitchKind kind,
        string? requestedRegion,
        CancellationToken cancellationToken,
        FailoverRequest? failover = null)
    {
        await _switching.WaitAsync(cancellationToken);
        try
        {
            return await SwitchOneAtATimeAsync(kind, requestedRegion, failover, cancellationToken);
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task<FailoverOutcome> SwitchOneAtATimeAsync(
        SwitchKind kind,
        string? requestedRegion,
        FailoverRequest? failover,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        var settings = await dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        // Read after any earlier switch finished, and none can start before this one does
        if (failover is not null && failover.HasLeft(settings.CurrentRegion))
        {
            LogAlreadyLeft(failover.FailedRegion);
            return FailoverOutcome.AlreadyLeft;
        }

        string? region = null;
        var regionApplied = false;
        string? publicIp = null;
        string? error = null;
        try
        {
            region = requestedRegion ?? await PickRegionAsync(kind, settings, gluetun, failover, cancellationToken);
            if (region is null)
            {
                LogNoFailoverRegion(failover!.FailedRegion);
                return FailoverOutcome.NoRegionLeft;
            }

            _progress = new VpnSwitchProgress(region, WaitingForRun: true, DateTime.UtcNow);
            await notifier.VpnChangedAsync();
            using (await gate.AcquireVpnLeaseAsync(cancellationToken))
            {
                _progress = _progress with { WaitingForRun = false };
                await notifier.VpnChangedAsync();

                LogSwitching(kind, region);
                await gluetun.SetRegionAsync(region, cancellationToken);

                regionApplied = true;
                // Saved before letting go of the gate, so a run waiting for it records where it runs
                await dbContext.VpnSettings
                    .Where(s => s.Id == VpnSettings.SingletonId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentRegion, region), CancellationToken.None);
                publicIp = await WaitForTunnelAsync(
                    scope.ServiceProvider.GetRequiredService<VpnProbeHttpClient>(), cancellationToken);
            }

            if (publicIp is null)
            {
                error = $"Switched to {region}, but the tunnel wasn't back up after {TunnelUpTimeout.TotalMinutes:0} minutes.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
        }
        finally
        {
            _progress = null;
        }

        // The settings may have been edited while the switch waited for a run
        await dbContext.Entry(settings).ReloadAsync(CancellationToken.None);
        var now = DateTime.UtcNow;
        if (regionApplied)
        {
            settings.CurrentRegion = region;
        }

        if (error is null && kind != SwitchKind.Reapply)
        {
            settings.LastSwitchTime = now;
            settings.ScheduleNextRotation(now, Random.Shared);
        }
        else if (error is not null && kind == SwitchKind.Rotation)
        {
            settings.NextRotationTime = now + RetryDelay;
        }

        settings.LastError = error;
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.VpnChangedAsync();

        if (error is null)
        {
            LogSwitched(region!, publicIp);
            return FailoverOutcome.Switched;
        }

        LogSwitchFailed(kind, error);
        return FailoverOutcome.Failed;
    }

    /// <summary>
    /// A random region from the regions of the provider gluetun is using: for a
    /// <see cref="SwitchKind.Replacement"/> any of them, for a <see cref="SwitchKind.Failover"/> one rotation may switch
    /// to that the watch hasn't failed in, otherwise one rotation may switch to.
    /// </summary>
    /// <returns>The region, or <see langword="null"/> if a failover has no region left.</returns>
    /// <exception cref="InvalidOperationException">No region is allowed.</exception>
    private async Task<string?> PickRegionAsync(
        SwitchKind kind,
        VpnSettings settings,
        GluetunHttpClient gluetun,
        FailoverRequest? failover,
        CancellationToken cancellationToken)
    {
        var regions = (await catalog.GetRegionsAsync(gluetun, cancellationToken)).Regions;
        if (regions.Count == 0)
        {
            throw new InvalidOperationException(configuration.LimitsRegions
                ? $"There are no regions to switch to: none of the regions in {EnvVars.GluetunServerRegions} is in "
                    + "gluetun's servers list for this VPN provider."
                : "There are no regions to switch to: gluetun's servers list has none for this VPN provider.");
        }

        if (kind == SwitchKind.Replacement)
        {
            return regions[Random.Shared.Next(regions.Count)];
        }

        if (failover is not null)
        {
            // Running out of regions is how a streak in every region ends, not something wrong with the VPN
            return settings.PickFailoverRegion(regions, failover.FailedRegions, Random.Shared);
        }

        return settings.PickRotationRegion(regions, Random.Shared)
            ?? throw new InvalidOperationException(DescribeNoRotationRegion(settings, regions));
    }

    /// <summary>
    /// Why <see cref="VpnSettings.PickRotationRegion"/> found no region in <paramref name="regions"/>.
    /// </summary>
    private string DescribeNoRotationRegion(VpnSettings settings, IReadOnlyList<string> regions)
    {
        var onlyTheCurrentRegion = regions.All(
            region => string.Equals(region, settings.CurrentRegion, StringComparison.OrdinalIgnoreCase));
        if (!onlyTheCurrentRegion)
        {
            return "There are no regions to rotate to: every region other than the current one is excluded.";
        }

        return configuration.LimitsRegions
            ? $"There are no regions to rotate to: {EnvVars.GluetunServerRegions} allows only {regions[0]}."
            : $"There are no regions to rotate to: gluetun's servers list has only {regions[0]} for this VPN provider.";
    }

    /// <summary>
    /// Polls until traffic gets through the VPN proxy, which gluetun's firewall blocks until the new tunnel is up.
    /// </summary>
    /// <returns>The new public IP, or <see langword="null"/> if the tunnel wasn't up within <see cref="TunnelUpTimeout"/>.</returns>
    private static async Task<string?> WaitForTunnelAsync(VpnProbeHttpClient probe, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TunnelUpTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            if (await probe.TryGetPublicIpAsync(cancellationToken) is { } publicIp)
            {
                return publicIp;
            }
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "VPN switch ({Kind}) to {Region}")]
    private partial void LogSwitching(SwitchKind kind, string region);

    [LoggerMessage(Level = LogLevel.Information, Message = "VPN switched to {Region}, public IP {PublicIp}")]
    private partial void LogSwitched(string region, string? publicIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "VPN switch ({Kind}) failed: {Error}")]
    private partial void LogSwitchFailed(SwitchKind kind, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "gluetun isn't on {Region}, probably after a restart; switching back")]
    private partial void LogReapplying(string region);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = $"The VPN is on {{Region}}, which isn't in {EnvVars.GluetunServerRegions}; switching to a region that is")]
    private partial void LogRegionNotAllowed(string region);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Backend hasn't switched the VPN yet; switching to a known region")]
    private partial void LogFirstSwitch();

    [LoggerMessage(Level = LogLevel.Information, Message = "The VPN already left {Region}, so it isn't failed over")]
    private partial void LogAlreadyLeft(string? region);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No region is left to fail over to from {Region}: the watch failed in every other one during its streak")]
    private partial void LogNoFailoverRegion(string? region);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check the VPN rotation")]
    private partial void LogPollFailed(Exception exception);
}

/// <summary>
/// How <see cref="VpnRotationService.FailOverAsync"/> ended.
/// </summary>
public enum FailoverOutcome
{
    /// <summary>The VPN switched to another region.</summary>
    Switched,

    /// <summary>Another switch had already left the failed region, so there was no need for one.</summary>
    AlreadyLeft,

    /// <summary>Every region the failover may pick was the current one or already failed in.</summary>
    NoRegionLeft,

    /// <summary>The switch failed, as <see cref="VpnSettings.LastError"/> says.</summary>
    Failed,

    /// <summary>The VPN isn't configured.</summary>
    Unavailable,
}

/// <summary>
/// A region switch in progress.
/// </summary>
/// <param name="WaitingForRun">Whether it's waiting for a VPN run to finish before restarting the tunnel.</param>
/// <param name="StartedTime">When the switch began, in UTC.</param>
public sealed record VpnSwitchProgress(string Region, bool WaitingForRun, DateTime StartedTime);
