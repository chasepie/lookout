using System.Threading.Channels;
using System.Threading.RateLimiting;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Makes every VPN region switch: rotations when <see cref="VpnSettings.NextRotationTime"/> comes due,
/// switches asked for with <see cref="RequestSwitch"/>, and reapplying <see cref="VpnSettings.CurrentRegion"/>
/// when gluetun has lost it, which happens when gluetun restarts.
/// </summary>
/// <remarks>
/// A switch holds <see cref="VpnServiceKeys.Gate"/> until the new tunnel is up, so it waits for a VPN run in progress and
/// VPN runs wait for it. A manual switch restarts the rotation timer. A failed rotation is retried after
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
        Reapply,
    }

    // Requested regions, null for a random one. Only the latest matters; one made while another waits replaces it
    private readonly Channel<string?> _requests = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private volatile VpnSwitchProgress? _progress;

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
    public void RequestSwitch(string? region) => _requests.Writer.TryWrite(region);

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
            s => s.Id == VpnSettings.SingletonId && s.RotationEnabled && s.NextRotationTime <= now,
            cancellationToken);
    }

    /// <summary>
    /// Switches back to <see cref="VpnSettings.CurrentRegion"/> if gluetun isn't restricted to it.
    /// gluetun keeps settings changed through its API only until it restarts.
    /// </summary>
    private async Task ReapplyIfLostAsync(CancellationToken cancellationToken)
    {
        string? currentRegion;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            currentRegion = await dbContext.VpnSettings
                .Where(s => s.Id == VpnSettings.SingletonId)
                .Select(s => s.CurrentRegion)
                .SingleAsync(cancellationToken);
            if (currentRegion is null)
            {
                return;
            }

            var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
            var gluetunSettings = await gluetun.GetVpnSettingsAsync(cancellationToken);
            if (gluetunSettings.Provider.ServerSelection.Regions is [var region]
                && string.Equals(region, currentRegion, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        LogReapplying(currentRegion);
        await SwitchAsync(SwitchKind.Reapply, currentRegion, cancellationToken);
    }

    /// <summary>
    /// Picks the region, switches gluetun to it and waits for the tunnel, then records the outcome.
    /// Failures are recorded in <see cref="VpnSettings.LastError"/> rather than thrown.
    /// </summary>
    private async Task SwitchAsync(SwitchKind kind, string? requestedRegion, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        var settings = await dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);

        string? region = null;
        var regionApplied = false;
        string? publicIp = null;
        string? error = null;
        try
        {
            region = requestedRegion ?? await PickRotationRegionAsync(settings, gluetun, cancellationToken);

            _progress = new VpnSwitchProgress(region, WaitingForRun: true, DateTime.UtcNow);
            await notifier.VpnChangedAsync();
            using (await gate.AcquireVpnLeaseAsync(cancellationToken))
            {
                _progress = _progress with { WaitingForRun = false };
                await notifier.VpnChangedAsync();

                LogSwitching(kind, region);
                await gluetun.SetRegionAsync(region, cancellationToken);

                regionApplied = true;
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
        }
        else
        {
            LogSwitchFailed(kind, error);
        }
    }

    /// <summary>
    /// A random region rotation may switch to, from the regions of the provider gluetun is using.
    /// </summary>
    /// <exception cref="InvalidOperationException">No region is allowed.</exception>
    private async Task<string> PickRotationRegionAsync(
        VpnSettings settings, GluetunHttpClient gluetun, CancellationToken cancellationToken)
    {
        var regions = (await catalog.GetRegionsAsync(gluetun, cancellationToken)).Regions;
        return settings.PickRotationRegion(regions, Random.Shared)
            ?? throw new InvalidOperationException(regions.Count == 0
                ? "There are no regions to switch to: gluetun's servers list has none for this VPN provider."
                : "There are no regions to rotate to: every region other than the current one is excluded.");
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check the VPN rotation")]
    private partial void LogPollFailed(Exception exception);
}

/// <summary>
/// A region switch in progress.
/// </summary>
/// <param name="WaitingForRun">Whether it's waiting for a VPN run to finish before restarting the tunnel.</param>
/// <param name="StartedTime">When the switch began, in UTC.</param>
public sealed record VpnSwitchProgress(string Region, bool WaitingForRun, DateTime StartedTime);
