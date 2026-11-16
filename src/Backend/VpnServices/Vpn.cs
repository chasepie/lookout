using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// The VPN, to the rest of the Backend: it knows gluetun, the regions, and the <see cref="VpnSettings"/> row, of which
/// it's the only reader and writer. The Owner sees and sets it up through <see cref="Controllers.VpnController"/>, and
/// <see cref="WatchRunWorker"/>, which decides when runs start and when switches happen, asks it which switch is due
/// and has it make them.
/// </summary>
/// <remarks>
/// <para>
/// A switch is planned, which picks its region, then made once no VPN run is in progress, which may be minutes later. A
/// switch is due when the Owner asks for one with <see cref="RequestSwitchAsync"/>, when
/// <see cref="VpnSettings.NextRotationTime"/> comes, and from a check of gluetun once a minute: a first switch when the
/// Backend has made none, so the region is always known, reapplying <see cref="VpnSettings.CurrentRegion"/> when gluetun
/// has lost it, which happens when gluetun restarts, and replacing it when <see cref="VpnConfiguration.ServerRegions"/>
/// no longer allows it. A watch's run in a region the watch avoids plans one away from it
/// (<see cref="PlanSwitchForWatchAsync"/>).
/// </para>
/// <para>
/// Every switch to a new region restarts the rotation timer. A failed rotation, or switch for a watch, is tried again
/// after <see cref="RetryDelay"/>. Its clock and delays are the host's <see cref="TimeProvider"/>. Nothing is due unless
/// <see cref="VpnConfiguration.IsEnabled"/>.
/// </para>
/// </remarks>
public partial class Vpn(
    IServiceScopeFactory scopeFactory,
    VpnConfiguration configuration,
    VpnRegionCatalog catalog,
    UpdatesNotifier notifier,
    TimeProvider timeProvider,
    ILogger<Vpn> logger)
{
    private static readonly TimeSpan TunnelPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TunnelUpTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    // The latest switch the Owner asked for, until it's planned; one asked for meanwhile replaces it
    private ManualRequest? _manualRequest;

    private sealed record ManualRequest(string? Region);

    // Read and written only while planning, which the scheduler does one plan at a time
    private DateTime _nextCheckTime = DateTime.MinValue;

    // After a switch for a watch fails, the next waits RetryDelay, rather than its run planning one every turn. Written
    // by a switch and read while planning, which never happen at once
    private DateTime _nextWatchSwitchTime = DateTime.MinValue;

    private volatile VpnSwitchProgress? _progress;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Whether the Backend knows where gluetun is. If not, VPN runs run with no region, and no switch is ever due.
    /// </summary>
    public bool IsEnabled => configuration.IsEnabled;

    /// <summary>
    /// The switch planned or in progress, or <see langword="null"/> if there's none.
    /// </summary>
    public VpnSwitchProgress? Progress => _progress;

    /// <summary>
    /// <see cref="VpnSettings.CurrentRegion"/>: the region the Backend last switched the VPN to, or
    /// <see langword="null"/> until its first switch.
    /// </summary>
    public async Task<string?> GetCurrentRegionAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        return await dbContext.VpnSettings
            .Where(s => s.Id == VpnSettings.SingletonId)
            .Select(s => s.CurrentRegion)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Plans the switch that's due, if any: the latest one the Owner asked for, then a rotation, then whatever the
    /// check of gluetun once a minute finds. A switch whose region can't be picked isn't planned, and its error is
    /// recorded as the VPN's.
    /// </summary>
    /// <returns>The switch, for <see cref="SwitchAsync"/>, or <see langword="null"/> if none is due.</returns>
    /// <exception cref="GluetunException">gluetun couldn't be reached to check it.</exception>
    public async Task<VpnSwitch?> PlanDueSwitchAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (Interlocked.Exchange(ref _manualRequest, null) is { } request)
        {
            return (await PlanAsync(SwitchKind.Manual, request.Region, cancellationToken)).Switch;
        }

        if (await IsRotationDueAsync(cancellationToken))
        {
            return (await PlanAsync(SwitchKind.Rotation, null, cancellationToken)).Switch;
        }

        if (UtcNow >= _nextCheckTime)
        {
            _nextCheckTime = UtcNow + CheckInterval;
            return await CheckGluetunAsync(cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Plans a switch away from the region the VPN is in, which the watch's run is about to start in and the watch
    /// avoids, to one the Owner hasn't excluded and the watch doesn't avoid, preferring one no other watch with a pending
    /// VPN run avoids. Plans none within <see cref="RetryDelay"/> of a switch for a watch that failed.
    /// </summary>
    /// <param name="avoidedByWatch">The regions the watch avoids, one of which the VPN is in.</param>
    public async Task<WatchSwitchPlan> PlanSwitchForWatchAsync(
        int watchId, IReadOnlyCollection<string> avoidedByWatch, CancellationToken cancellationToken)
    {
        if (UtcNow < _nextWatchSwitchTime)
        {
            return new WatchSwitchPlan(null, NoRegionLeft: false);
        }

        var planned = await PlanAsync(
            SwitchKind.ForWatch, null, cancellationToken, forWatchId: watchId, avoidedByWatch: avoidedByWatch);
        return new WatchSwitchPlan(planned.Switch, planned.NoRegionLeft);
    }

    /// <summary>
    /// The settings, the switch planned or in progress, and the tunnel's public IP as gluetun reports it, or why gluetun
    /// couldn't be reached. If the VPN isn't set up, says so and nothing else.
    /// </summary>
    public async Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!configuration.IsEnabled)
        {
            return new VpnStatus(false, null, null, null, null);
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var settings = await dbContext.VpnSettings.AsNoTracking()
            .SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        GluetunPublicIp? publicIp = null;
        string? gluetunError = null;
        try
        {
            publicIp = await scope.ServiceProvider.GetRequiredService<GluetunHttpClient>()
                .GetPublicIpAsync(cancellationToken);
        }
        catch (GluetunException ex)
        {
            gluetunError = ex.Message;
        }

        return new VpnStatus(true, settings, Progress, publicIp, gluetunError);
    }

    /// <summary>
    /// The regions the VPN provider offers, from gluetun's servers list, limited to
    /// <see cref="VpnConfiguration.ServerRegions"/>.
    /// </summary>
    /// <returns>The regions, or <see langword="null"/> if the VPN isn't set up.</returns>
    /// <exception cref="GluetunException">gluetun couldn't be reached.</exception>
    public async Task<VpnRegions?> GetRegionsAsync(CancellationToken cancellationToken) =>
        configuration.IsEnabled ? await catalog.GetRegionsAsync(cancellationToken) : null;

    /// <summary>
    /// Applies the Owner's rotation settings, and tells clients through <see cref="IUpdatesClient.VpnChanged"/>.
    /// </summary>
    /// <returns>The settings now, or <see langword="null"/> if the VPN isn't set up.</returns>
    public async Task<VpnSettings?> UpdateSettingsAsync(VpnSettingsDto dto, CancellationToken cancellationToken)
    {
        if (!configuration.IsEnabled)
        {
            return null;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var settings = await dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        settings.Update(dto, UtcNow, Random.Shared);
        await dbContext.SaveChangesAsync(cancellationToken);
        await notifier.VpnChangedAsync();
        return settings;
    }

    /// <summary>
    /// Queues a switch the Owner asked for, planned within a second unless another switch is planned or in progress,
    /// and made once the VPN runs in progress have finished. Clients hear how it went through
    /// <see cref="IUpdatesClient.VpnChanged"/>.
    /// </summary>
    /// <param name="region">
    /// The region the Owner typed, matched to <see cref="GetRegionsAsync"/>'s ignoring case, or
    /// <see langword="null"/> or blank for a random one. Excluded regions are allowed: exclusions only apply to
    /// rotation.
    /// </param>
    /// <exception cref="GluetunException">gluetun couldn't be reached to check the region.</exception>
    public async Task<ManualSwitchResult> RequestSwitchAsync(string? region, CancellationToken cancellationToken)
    {
        if (!configuration.IsEnabled)
        {
            return ManualSwitchResult.NotSetUp;
        }

        string? listed = null;
        if (!string.IsNullOrWhiteSpace(region))
        {
            // Matches gluetun's case-insensitive check, but sends the list's spelling, which reapplying compares to
            listed = (await catalog.GetRegionsAsync(cancellationToken)).Regions
                .FirstOrDefault(r => string.Equals(r, region.Trim(), StringComparison.OrdinalIgnoreCase));
            if (listed is null)
            {
                return ManualSwitchResult.UnknownRegion;
            }
        }

        Volatile.Write(ref _manualRequest, new ManualRequest(listed));
        return ManualSwitchResult.Queued;
    }

    /// <summary>
    /// Makes a planned switch, unless it's no longer needed: switches gluetun to its region and waits for the tunnel,
    /// then records the outcome. Failures are recorded in <see cref="VpnSettings.LastError"/> rather than thrown. No VPN
    /// run may be in progress, or start, until it returns, since restarting the tunnel would cut it off, and the region
    /// it would record would be wrong.
    /// </summary>
    /// <param name="planned">From <see cref="PlanDueSwitchAsync"/> or <see cref="PlanSwitchForWatchAsync"/>.</param>
    public async Task SwitchAsync(VpnSwitch planned, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
            var settings = await dbContext.VpnSettings.SingleAsync(
                s => s.Id == VpnSettings.SingletonId, cancellationToken);

            // Planned while VPN runs were in progress, maybe minutes ago, and the Owner may have changed what made it due
            // since. Only switches change the region, so nothing else can have overtaken it
            if (!await IsStillNeededAsync(planned, dbContext, settings, cancellationToken))
            {
                LogNoLongerNeeded(planned.Kind);
                return;
            }

            var regionApplied = false;
            string? publicIp = null;
            string? error = null;
            try
            {
                _progress = new VpnSwitchProgress(planned.Region, WaitingForRun: false, planned.PlannedTime);
                await notifier.VpnChangedAsync();

                LogSwitching(planned.Kind, planned.Region);
                await gluetun.SetRegionAsync(planned.Region, cancellationToken);

                regionApplied = true;
                // Saved before the tunnel is up, so a run started after the switch records where it runs even if the
                // outcome can't be saved
                await dbContext.VpnSettings
                    .Where(s => s.Id == VpnSettings.SingletonId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.CurrentRegion, planned.Region), CancellationToken.None);
                publicIp = await WaitForTunnelAsync(
                    scope.ServiceProvider.GetRequiredService<VpnProbeHttpClient>(), cancellationToken);

                if (publicIp is null)
                {
                    error = $"Switched to {planned.Region}, but the tunnel wasn't back up after "
                        + $"{TunnelUpTimeout.TotalMinutes:0} minutes.";
                }
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error = ex.Message;
            }

            _progress = null;
            await RecordOutcomeAsync(
                planned.Kind, dbContext, settings, regionApplied ? planned.Region : null, publicIp, error);
        }
        finally
        {
            // However it ended, so clients don't keep showing it
            if (_progress is not null)
            {
                _progress = null;
                await notifier.VpnChangedAsync();
            }
        }
    }

    /// <summary>
    /// Whether a planned switch should still be made: a rotation only while one is still due, since the Owner may have
    /// turned rotation off or put it back, and a switch for a watch only while the watch still uses the VPN, has a
    /// pending run, and avoids the region, since the Owner may have turned its VPN off, deleted its runs, or cleared the
    /// region.
    /// </summary>
    private async Task<bool> IsStillNeededAsync(
        VpnSwitch planned,
        LookoutDbContext dbContext,
        VpnSettings settings,
        CancellationToken cancellationToken)
    {
        return planned.Kind switch
        {
            SwitchKind.Rotation => settings.RotationEnabled && settings.NextRotationTime <= UtcNow,
            SwitchKind.ForWatch => await dbContext.Watches.AnyAsync(
                    w => w.Id == planned.ForWatchId
                        && w.UseVpn
                        && w.WatchRuns.Any(wr => wr.Status == WatchRunStatus.Created),
                    cancellationToken)
                && (await dbContext.GetAvoidedRegionsAsync(planned.ForWatchId!.Value, cancellationToken))
                    .Contains(settings.CurrentRegion, StringComparer.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private async Task<bool> IsRotationDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = UtcNow;
        return await dbContext.VpnSettings.AnyAsync(
            s => s.Id == VpnSettings.SingletonId
                && s.RotationEnabled
                && s.NextRotationTime <= now,
            cancellationToken);
    }

    /// <summary>
    /// Plans a switch back to <see cref="VpnSettings.CurrentRegion"/> if gluetun isn't restricted to it.
    /// gluetun keeps settings changed through its API only until it restarts. If the region is no longer one of
    /// <see cref="VpnConfiguration.ServerRegions"/>, plans one to a random one that is instead. If the Backend hasn't
    /// switched yet, plans its first switch, and tries again next time if that fails.
    /// </summary>
    /// <exception cref="GluetunException">gluetun couldn't be reached.</exception>
    private async Task<VpnSwitch?> CheckGluetunAsync(CancellationToken cancellationToken)
    {
        if (await GetCurrentRegionAsync(cancellationToken) is not { } currentRegion)
        {
            // While gluetun starts up this throws, which the scheduler only logs, rather than the switch showing it as
            // the VPN's error every minute
            var gluetunSettings = await GetGluetunSettingsAsync(cancellationToken);
            LogFirstSwitch();
            return (await PlanAsync(SwitchKind.First, null, cancellationToken, gluetunSettings: gluetunSettings)).Switch;
        }

        if (!configuration.AllowsRegion(currentRegion))
        {
            // GLUETUN_SERVER_REGIONS changed since the switch to it, and reapplying it would undo the change
            LogRegionNotAllowed(currentRegion);
            return (await PlanAsync(SwitchKind.Replacement, null, cancellationToken)).Switch;
        }

        var restrictedTo = (await GetGluetunSettingsAsync(cancellationToken)).Provider.ServerSelection.Regions;
        if (restrictedTo is [var region] && string.Equals(region, currentRegion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        LogReapplying(currentRegion);
        return (await PlanAsync(SwitchKind.Reapply, currentRegion, cancellationToken)).Switch;
    }

    /// <exception cref="GluetunException">gluetun couldn't be reached.</exception>
    private async Task<GluetunVpnSettings> GetGluetunSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        return await gluetun.GetVpnSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Picks the switch's region, unless <paramref name="requestedRegion"/> is it, and shows the switch as waiting for
    /// the VPN runs in progress. A region that can't be picked is recorded as the VPN's error.
    /// </summary>
    /// <param name="forWatchId">For a <see cref="SwitchKind.ForWatch"/>, the watch whose run waits to leave a region it
    /// avoids.</param>
    /// <param name="avoidedByWatch">For a <see cref="SwitchKind.ForWatch"/>, the regions the watch avoids.</param>
    /// <param name="gluetunSettings">What gluetun said it's using, if it was just asked, so it isn't asked again.</param>
    /// <returns>The switch, if one is planned, and whether a watch has no region left.</returns>
    private async Task<(VpnSwitch? Switch, bool NoRegionLeft)> PlanAsync(
        SwitchKind kind,
        string? requestedRegion,
        CancellationToken cancellationToken,
        int? forWatchId = null,
        IReadOnlyCollection<string>? avoidedByWatch = null,
        GluetunVpnSettings? gluetunSettings = null)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var settings = await dbContext.VpnSettings.SingleAsync(
            s => s.Id == VpnSettings.SingletonId, cancellationToken);

        string? region;
        try
        {
            region = requestedRegion ?? await PickRegionAsync(
                kind, dbContext, settings, forWatchId, avoidedByWatch ?? [], gluetunSettings, cancellationToken);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Such as gluetun not answering: whether a watch has a region left isn't known then, so its run waits rather
            // than crashing for the VPN's problem
            await RecordOutcomeAsync(kind, dbContext, settings, null, null, ex.Message);
            return (null, false);
        }

        if (region is null)
        {
            LogNoRegionForWatch(forWatchId);
            return (null, true);
        }

        if (forWatchId is { } watchId)
        {
            LogLeavingAvoidedRegion(watchId, settings.CurrentRegion);
        }

        var planned = new VpnSwitch(kind, region, forWatchId, UtcNow);
        _progress = new VpnSwitchProgress(region, WaitingForRun: true, planned.PlannedTime);
        try
        {
            await notifier.VpnChangedAsync();
        }
        catch
        {
            // The plan is lost, so it mustn't go on showing
            _progress = null;
            throw;
        }

        return (planned, false);
    }

    /// <summary>
    /// Saves how a switch that got as far as picking a region went, or why it didn't.
    /// </summary>
    /// <param name="appliedRegion">The region gluetun was switched to, if it got that far.</param>
    private async Task RecordOutcomeAsync(
        SwitchKind kind,
        LookoutDbContext dbContext,
        VpnSettings settings,
        string? appliedRegion,
        string? publicIp,
        string? error)
    {
        // The Owner may have edited the settings while the switch was made
        await dbContext.Entry(settings).ReloadAsync(CancellationToken.None);
        var now = UtcNow;
        if (appliedRegion is not null)
        {
            settings.CurrentRegion = appliedRegion;
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
        else if (error is not null && kind == SwitchKind.ForWatch)
        {
            _nextWatchSwitchTime = now + RetryDelay;
        }

        settings.LastError = error;
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.VpnChangedAsync();

        if (error is null)
        {
            LogSwitched(appliedRegion!, publicIp);
        }
        else
        {
            LogSwitchFailed(kind, error);
        }
    }

    /// <summary>
    /// A random region from the regions of the provider gluetun is using: for a <see cref="SwitchKind.Replacement"/> any
    /// of them, for a <see cref="SwitchKind.ForWatch"/> one the watch may run in, preferring one no other watch with a
    /// pending VPN run avoids, and otherwise one rotation may switch to, preferring one no watch using the VPN avoids.
    /// </summary>
    /// <param name="avoidedByWatch">For a <see cref="SwitchKind.ForWatch"/>, the regions the watch avoids.</param>
    /// <param name="gluetunSettings">What gluetun said it's using, if it was just asked.</param>
    /// <returns>The region, or <see langword="null"/> if the watch has no region left.</returns>
    /// <exception cref="InvalidOperationException">No region is allowed.</exception>
    private async Task<string?> PickRegionAsync(
        SwitchKind kind,
        LookoutDbContext dbContext,
        VpnSettings settings,
        int? forWatchId,
        IReadOnlyCollection<string> avoidedByWatch,
        GluetunVpnSettings? gluetunSettings,
        CancellationToken cancellationToken)
    {
        var regions = (gluetunSettings is not null
            ? catalog.GetRegions(gluetunSettings)
            : await catalog.GetRegionsAsync(cancellationToken)).Regions;
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

        if (forWatchId is { } watchId)
        {
            // Regions the switch would otherwise move the next of these runs out of again
            var avoidedByOthers = await dbContext.AvoidedRegions
                .Where(a => a.WatchId != watchId
                    && a.Watch.UseVpn
                    && a.Watch.WatchRuns.Any(wr => wr.Status == WatchRunStatus.Created))
                .Select(a => a.Region)
                .Distinct()
                .ToListAsync(cancellationToken);
            // Running out of regions is the watch's problem, which its run reports, not something wrong with the VPN
            return settings.PickRegionForWatch(regions, avoidedByWatch, avoidedByOthers, Random.Shared);
        }

        var avoidedByWatches = await dbContext.AvoidedRegions
            .Where(a => a.Watch.UseVpn)
            .Select(a => a.Region)
            .Distinct()
            .ToListAsync(cancellationToken);
        return settings.PickRotationRegion(regions, avoidedByWatches, Random.Shared)
            ?? throw new InvalidOperationException(DescribeNoRotationRegion(kind, settings, regions));
    }

    /// <summary>
    /// Why <see cref="VpnSettings.PickRotationRegion"/> found no region in <paramref name="regions"/>.
    /// </summary>
    private string DescribeNoRotationRegion(SwitchKind kind, VpnSettings settings, IReadOnlyList<string> regions)
    {
        // A first switch may come with rotation off, so it mustn't speak of rotating
        var noRegions = kind switch
        {
            SwitchKind.Rotation => "There are no regions to rotate to",
            _ => "There are no regions to switch to",
        };
        if (settings.CurrentRegion is null)
        {
            return $"{noRegions}: every region is excluded.";
        }

        var onlyTheCurrentRegion = regions.All(
            region => string.Equals(region, settings.CurrentRegion, StringComparison.OrdinalIgnoreCase));
        if (!onlyTheCurrentRegion)
        {
            return $"{noRegions}: every region other than the current one is excluded.";
        }

        return configuration.LimitsRegions
            ? $"{noRegions}: {EnvVars.GluetunServerRegions} allows only {regions[0]}."
            : $"{noRegions}: gluetun's servers list has only {regions[0]} for this VPN provider.";
    }

    /// <summary>
    /// Polls until traffic gets through the VPN proxy, which gluetun's firewall blocks until the new tunnel is up.
    /// </summary>
    /// <returns>The new public IP, or <see langword="null"/> if the tunnel wasn't up within <see cref="TunnelUpTimeout"/>.</returns>
    private async Task<string?> WaitForTunnelAsync(VpnProbeHttpClient probe, CancellationToken cancellationToken)
    {
        var deadline = UtcNow + TunnelUpTimeout;
        while (UtcNow < deadline)
        {
            await Task.Delay(TunnelPollInterval, timeProvider, cancellationToken);
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The VPN switch ({Kind}) isn't needed any more, since the Owner changed what made it due")]
    private partial void LogNoLongerNeeded(SwitchKind kind);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "A run of watch {WatchId} waits for the VPN to leave {Region}, which the watch avoids")]
    private partial void LogLeavingAvoidedRegion(int watchId, string? region);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The VPN isn't switched for watch {WatchId}: it avoids every region it may switch to")]
    private partial void LogNoRegionForWatch(int? watchId);
}

/// <summary>
/// What a region switch is for.
/// </summary>
internal enum SwitchKind
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
    /// Away from a region a watch avoids, which its run is about to start in, to one the Owner hasn't excluded and
    /// the watch doesn't avoid.
    /// </summary>
    ForWatch,

    /// <summary>
    /// To a region rotation may switch to, when the Backend hasn't switched yet. Until then gluetun picks a region
    /// of its own and doesn't say which, so runs couldn't record theirs or check it against what their watch avoids.
    /// </summary>
    First,
}

/// <summary>
/// A region switch <see cref="Vpn"/> has planned, with its region picked, for <see cref="Vpn.SwitchAsync"/> to make.
/// </summary>
public sealed class VpnSwitch
{
    internal VpnSwitch(SwitchKind kind, string region, int? forWatchId, DateTime plannedTime)
    {
        Kind = kind;
        Region = region;
        ForWatchId = forWatchId;
        PlannedTime = plannedTime;
    }

    internal SwitchKind Kind { get; }

    /// <summary>
    /// The region it switches to.
    /// </summary>
    public string Region { get; }

    /// <summary>
    /// For a switch away from a region a watch avoids, the watch.
    /// </summary>
    internal int? ForWatchId { get; }

    internal DateTime PlannedTime { get; }
}

/// <summary>
/// What <see cref="Vpn.PlanSwitchForWatchAsync"/> made of a run in a region its watch avoids. With neither, the run
/// waits, as other runs may start meanwhile: a switch for a watch failed less than a minute ago, or the region couldn't be
/// picked, which is recorded as the VPN's error.
/// </summary>
/// <param name="Switch">The switch away from the region, if one is planned.</param>
/// <param name="NoRegionLeft">
/// Whether the watch avoids every region the VPN may switch to for it, so its run can't start until the Owner clears one.
/// </param>
public sealed record WatchSwitchPlan(VpnSwitch? Switch, bool NoRegionLeft);

/// <summary>
/// How <see cref="Vpn.RequestSwitchAsync"/> took a switch the Owner asked for.
/// </summary>
public enum ManualSwitchResult
{
    /// <summary>The switch is queued.</summary>
    Queued,

    /// <summary>The VPN isn't set up.</summary>
    NotSetUp,

    /// <summary>The region isn't one of <see cref="Vpn.GetRegionsAsync"/>'s.</summary>
    UnknownRegion,
}

/// <param name="Configured">Whether the Backend knows where gluetun is. If not, every other field is null.</param>
/// <param name="Switch">The region switch planned or in progress, if any.</param>
/// <param name="PublicIp">The tunnel's public IP, or null while the tunnel is down or gluetun is unreachable.</param>
/// <param name="GluetunError">Why gluetun couldn't be reached, if it couldn't.</param>
public sealed record VpnStatus(
    bool Configured,
    VpnSettings? Settings,
    VpnSwitchProgress? Switch,
    GluetunPublicIp? PublicIp,
    string? GluetunError);

/// <summary>
/// A region switch planned or in progress.
/// </summary>
/// <param name="WaitingForRun">Whether it's waiting for the VPN runs in progress to finish before restarting the tunnel.</param>
/// <param name="StartedTime">When the switch was planned, in UTC.</param>
public sealed record VpnSwitchProgress(string Region, bool WaitingForRun, DateTime StartedTime);
