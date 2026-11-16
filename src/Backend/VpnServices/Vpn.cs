using System.Threading.Channels;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// The VPN, to the rest of the Backend: runs of watches with <c>UseVpn</c> use it through a <see cref="VpnRun"/>, the
/// Owner sees and sets it up through <see cref="Controllers.VpnController"/>, and it makes every region switch:
/// rotations when <see cref="VpnSettings.NextRotationTime"/> comes due, switches the Owner asks for with
/// <see cref="RequestSwitchAsync"/>, switches away from a region a watch avoids when its run is about to start there
/// (<see cref="TryStartRunAsync"/>), a first switch when it has made none, so the region is always known, reapplying
/// <see cref="VpnSettings.CurrentRegion"/> when gluetun has lost it, which happens when gluetun restarts, and replacing it when
/// <see cref="VpnConfiguration.ServerRegions"/> no longer allows it. It's the only reader and writer of the
/// <see cref="VpnSettings"/> row.
/// </summary>
/// <remarks>
/// <para>
/// Runs and switches are kept apart by a <see cref="VpnGate"/> of its own: each <see cref="VpnRun"/> holds a share of
/// it, which any number of runs hold at once, and a switch holds it to itself until the new tunnel is up and it has
/// saved how it went, whether it worked or not, so it waits for the VPN runs in progress, and VPN runs that haven't
/// started wait for it from when it starts waiting. A run reads its region while holding its share, and a switch saves
/// the new region before letting go, so the region a run records is the one it ran in. Every switch is made by this
/// service's loop, one at a time, so each picks its region from where the last one left the VPN.
/// </para>
/// <para>
/// A run checks its watch's avoided regions as it starts, rather than anything being carried from the run that asked to
/// avoid one: if the VPN is in one, the run lets go and asks for a switch for its watch, and a request that's dropped,
/// since only the latest is kept, is made again by the run's next try.
/// </para>
/// <para>
/// Every switch to a new region restarts the rotation timer. A failed rotation is retried after
/// <see cref="RetryDelay"/>. Its clock, timers and delays are the host's <see cref="TimeProvider"/>. Switches nothing
/// unless <see cref="VpnConfiguration.IsEnabled"/>, though runs still get a <see cref="VpnRun"/>.
/// </para>
/// </remarks>
public partial class Vpn(
    IServiceScopeFactory scopeFactory,
    VpnConfiguration configuration,
    VpnRegionCatalog catalog,
    UpdatesNotifier notifier,
    TimeProvider timeProvider,
    ILogger<Vpn> logger) : BackgroundService
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

    private readonly VpnGate _gate = new();

    // Requested regions, null for a random one. Only the latest matters; one made while another waits replaces it
    private readonly Channel<string?> _requests = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // Watches whose runs wait to leave a region they avoid. Only the latest is kept: a dropped one asks again
    private readonly Channel<int> _watchSwitches = Channel.CreateBounded<int>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // After a switch for a watch fails, the next waits RetryDelay, rather than its run asking again every poll. Only the
    // loop reads and writes it
    private DateTime _nextWatchSwitchTime = DateTime.MinValue;

    private volatile VpnSwitchProgress? _progress;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The switch in progress, or <see langword="null"/> if there's none.
    /// </summary>
    public VpnSwitchProgress? Progress => _progress;

    /// <summary>
    /// Whether no VPN run can start now, since a switch is waiting or in progress.
    /// </summary>
    public bool HoldsUpRuns => _gate.HoldsUpRuns;

    /// <summary>
    /// Raised when a switch is done, so VPN runs held up by it can start now.
    /// Raised on the thread that ended the switch; handlers must be quick and not throw.
    /// </summary>
    public event Action? RunsMayStart
    {
        add => _gate.RunsMayEnter += value;
        remove => _gate.RunsMayEnter -= value;
    }

    /// <summary>
    /// Lets a run of a watch with <c>UseVpn</c> use the VPN, unless a switch is waiting or in progress, the Backend
    /// hasn't made its first switch, so the region isn't known, or the VPN is in a region the watch avoids. No switch
    /// starts until the run disposes the <see cref="VpnRun"/>, so the run stays in <see cref="VpnRun.Region"/>
    /// throughout. In a region the watch avoids, it asks for a switch away from it for the watch.
    /// </summary>
    /// <param name="watchId">The run's watch.</param>
    public async Task<VpnRunStart> TryStartRunAsync(int watchId, CancellationToken cancellationToken)
    {
        if (_gate.TryEnterRun() is not { } share)
        {
            return new VpnRunStart(VpnRunStartOutcome.HeldUp);
        }

        try
        {
            if (!configuration.IsEnabled)
            {
                return new VpnRunStart(VpnRunStartOutcome.Started, new VpnRun(share, null));
            }

            // Read while holding the share, so no switch changes it during the run
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var settings = await dbContext.VpnSettings.AsNoTracking()
                .SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
            if (settings.CurrentRegion is not { } region)
            {
                share.Dispose();
                return new VpnRunStart(VpnRunStartOutcome.HeldUp);
            }

            var avoided = await GetAvoidedRegionsAsync(dbContext, watchId, cancellationToken);
            if (!avoided.Contains(region, StringComparer.OrdinalIgnoreCase))
            {
                return new VpnRunStart(VpnRunStartOutcome.Started, new VpnRun(share, region));
            }

            share.Dispose();
            if (await HasNoRegionLeftAsync(settings, avoided, cancellationToken))
            {
                return new VpnRunStart(VpnRunStartOutcome.NoRegionLeft);
            }

            LogLeavingAvoidedRegion(watchId, region);
            _watchSwitches.Writer.TryWrite(watchId);
            return new VpnRunStart(VpnRunStartOutcome.InAvoidedRegion);
        }
        catch
        {
            share.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether a watch that avoids <paramref name="avoided"/> is known to have no region left. Not when the regions
    /// can't be read, which the switch then reports as the VPN's error, rather than the watch's runs crashing for it.
    /// </summary>
    private async Task<bool> HasNoRegionLeftAsync(
        VpnSettings settings, IReadOnlyCollection<string> avoided, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> regions;
        try
        {
            regions = (await catalog.GetRegionsAsync(cancellationToken)).Regions;
        }
        catch (GluetunException)
        {
            return false;
        }

        return regions.Count > 0 && !settings.HasRegionLeftFor(regions, avoided);
    }

    private static Task<List<string>> GetAvoidedRegionsAsync(
        LookoutDbContext dbContext, int watchId, CancellationToken cancellationToken) =>
        dbContext.AvoidedRegions
            .Where(a => a.WatchId == watchId)
            .Select(a => a.Region)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The settings, the switch in progress, and the tunnel's public IP as gluetun reports it, or why gluetun couldn't
    /// be reached. If the VPN isn't set up, says so and nothing else.
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
    /// Queues a switch the Owner asked for, made within a second unless another switch or VPN runs are in progress.
    /// Clients hear how it went through <see cref="IUpdatesClient.VpnChanged"/>.
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

        _requests.Writer.TryWrite(listed);
        return ManualSwitchResult.Queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.IsEnabled)
        {
            return;
        }

        var nextReconcileTime = DateTime.MinValue;
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        do
        {
            try
            {
                if (_requests.Reader.TryRead(out var requestedRegion))
                {
                    await SwitchAsync(SwitchKind.Manual, requestedRegion, stoppingToken);
                }
                else if (UtcNow >= _nextWatchSwitchTime && _watchSwitches.Reader.TryRead(out var watchId))
                {
                    await SwitchAsync(SwitchKind.ForWatch, null, stoppingToken, forWatchId: watchId);
                }
                else if (await IsRotationDueAsync(stoppingToken))
                {
                    await SwitchAsync(SwitchKind.Rotation, null, stoppingToken);
                }
                else if (UtcNow >= nextReconcileTime)
                {
                    nextReconcileTime = UtcNow + ReconcileInterval;
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
        var now = UtcNow;
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
    /// switched yet, makes its first switch, and tries again next time if that fails. It doesn't wait for switches,
    /// so the switch it asks for is dropped if another one changed the region meanwhile.
    /// </summary>
    private async Task ReapplyIfLostAsync(CancellationToken cancellationToken)
    {
        // The checks each use a scope of their own, so none is held through a switch, which can wait minutes
        if (await GetCurrentRegionAsync(cancellationToken) is not { } currentRegion)
        {
            // While gluetun starts up this throws, which the poll only logs, rather than the switch showing it as the
            // VPN's error every minute
            await GetGluetunSettingsAsync(cancellationToken);
            LogFirstSwitch();
            await SwitchAsync(SwitchKind.First, null, cancellationToken, checkedRegion: null);
            return;
        }

        if (!configuration.AllowsRegion(currentRegion))
        {
            // GLUETUN_SERVER_REGIONS changed since the switch to it, and reapplying it would undo the change
            LogRegionNotAllowed(currentRegion);
            await SwitchAsync(SwitchKind.Replacement, null, cancellationToken, checkedRegion: currentRegion);
        }
        else if (!await IsGluetunRestrictedToAsync(currentRegion, cancellationToken))
        {
            LogReapplying(currentRegion);
            await SwitchAsync(SwitchKind.Reapply, currentRegion, cancellationToken, checkedRegion: currentRegion);
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
        var gluetunSettings = await GetGluetunSettingsAsync(cancellationToken);
        return gluetunSettings.Provider.ServerSelection.Regions is [var restrictedTo]
            && string.Equals(restrictedTo, region, StringComparison.OrdinalIgnoreCase);
    }

    /// <exception cref="GluetunException">gluetun couldn't be reached.</exception>
    private async Task<GluetunVpnSettings> GetGluetunSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        return await gluetun.GetVpnSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Picks the region, switches gluetun to it and waits for the tunnel, then records the outcome.
    /// Failures are recorded in <see cref="VpnSettings.LastError"/> rather than thrown.
    /// </summary>
    /// <param name="checkedRegion">For a switch the check of gluetun's region asks for, the VPN's region the check
    /// read.</param>
    /// <param name="forWatchId">For a <see cref="SwitchKind.ForWatch"/>, the watch whose run waits to leave a region it
    /// avoids.</param>
    private async Task SwitchAsync(
        SwitchKind kind,
        string? requestedRegion,
        CancellationToken cancellationToken,
        string? checkedRegion = null,
        int? forWatchId = null)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
        var settings = await dbContext.VpnSettings.SingleAsync(
            s => s.Id == VpnSettings.SingletonId, cancellationToken);

        // The check that asked for it read the VPN's region without waiting for switches, so one may have changed it
        // since, and this would undo it
        if (kind is SwitchKind.Reapply or SwitchKind.Replacement or SwitchKind.First
            && !string.Equals(settings.CurrentRegion, checkedRegion, StringComparison.OrdinalIgnoreCase))
        {
            LogOvertaken(kind);
            return;
        }

        // Found due before the switch ahead of it, which may have restarted the timer, or the Owner may have turned
        // rotation off meanwhile
        if (kind == SwitchKind.Rotation && !(settings.RotationEnabled && settings.NextRotationTime <= UtcNow))
        {
            LogOvertaken(kind);
            return;
        }

        IReadOnlyList<string> avoidedByWatch = [];
        if (forWatchId is { } watchId)
        {
            // Asked for before the switch ahead of it, which may have left the region, or the Owner may have cleared it
            avoidedByWatch = await GetAvoidedRegionsAsync(dbContext, watchId, cancellationToken);
            if (!avoidedByWatch.Contains(settings.CurrentRegion, StringComparer.OrdinalIgnoreCase))
            {
                LogOvertaken(kind);
                return;
            }
        }

        string? region;
        try
        {
            region = requestedRegion
                ?? await PickRegionAsync(kind, dbContext, settings, forWatchId, avoidedByWatch, cancellationToken);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RecordOutcomeAsync(kind, dbContext, settings, null, null, ex.Message);
            return;
        }

        if (region is null)
        {
            // The run's next try finds the watch has no region left, and ends the run, which says why
            LogNoRegionForWatch(forWatchId);
            return;
        }

        _progress = new VpnSwitchProgress(region, WaitingForRun: true, UtcNow);
        await notifier.VpnChangedAsync();
        IDisposable hold;
        try
        {
            hold = await _gate.EnterSwitchAsync(cancellationToken);
        }
        catch
        {
            _progress = null;
            throw;
        }

        // Let go of once the outcome is saved, however the switch ends, so no VPN run starts before then
        using (hold)
        {
            // The Owner may have cleared the region while the switch waited for the VPN runs in progress
            if (forWatchId is { } stillAvoiding
                && !(await GetAvoidedRegionsAsync(dbContext, stillAvoiding, cancellationToken))
                    .Contains(settings.CurrentRegion, StringComparer.OrdinalIgnoreCase))
            {
                _progress = null;
                await notifier.VpnChangedAsync();
                LogOvertaken(kind);
                return;
            }

            var regionApplied = false;
            string? publicIp = null;
            string? error = null;
            try
            {
                _progress = _progress with { WaitingForRun = false };
                await notifier.VpnChangedAsync();

                LogSwitching(kind, region);
                await gluetun.SetRegionAsync(region, cancellationToken);

                regionApplied = true;
                // Saved while holding the gate, so a run that starts after it records where it runs
                await dbContext.VpnSettings
                    .Where(s => s.Id == VpnSettings.SingletonId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentRegion, region), CancellationToken.None);
                publicIp = await WaitForTunnelAsync(
                    scope.ServiceProvider.GetRequiredService<VpnProbeHttpClient>(), cancellationToken);

                if (publicIp is null)
                {
                    error = $"Switched to {region}, but the tunnel wasn't back up after "
                        + $"{TunnelUpTimeout.TotalMinutes:0} minutes.";
                }
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error = ex.Message;
            }
            finally
            {
                _progress = null;
            }

            await RecordOutcomeAsync(kind, dbContext, settings, regionApplied ? region : null, publicIp, error);
        }
    }

    /// <summary>
    /// Saves how a switch that got as far as picking a region went.
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
        // The settings may have been edited while the switch waited for a run
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
    /// <returns>The region, or <see langword="null"/> if the watch has no region left.</returns>
    /// <exception cref="InvalidOperationException">No region is allowed.</exception>
    private async Task<string?> PickRegionAsync(
        SwitchKind kind,
        LookoutDbContext dbContext,
        VpnSettings settings,
        int? forWatchId,
        IReadOnlyList<string> avoidedByWatch,
        CancellationToken cancellationToken)
    {
        var regions = (await catalog.GetRegionsAsync(cancellationToken)).Regions;
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
            await Task.Delay(PollInterval, timeProvider, cancellationToken);
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
        Message = "The VPN changed since the {Kind} was asked for, so it isn't needed")]
    private partial void LogOvertaken(SwitchKind kind);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "A run of watch {WatchId} waits for the VPN to leave {Region}, which the watch avoids")]
    private partial void LogLeavingAvoidedRegion(int watchId, string region);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The VPN isn't switched for watch {WatchId}: it avoids every region it may switch to")]
    private partial void LogNoRegionForWatch(int? watchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check the VPN rotation")]
    private partial void LogPollFailed(Exception exception);
}

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
/// <param name="Switch">The region switch in progress, if any.</param>
/// <param name="PublicIp">The tunnel's public IP, or null while the tunnel is down or gluetun is unreachable.</param>
/// <param name="GluetunError">Why gluetun couldn't be reached, if it couldn't.</param>
public sealed record VpnStatus(
    bool Configured,
    VpnSettings? Settings,
    VpnSwitchProgress? Switch,
    GluetunPublicIp? PublicIp,
    string? GluetunError);

/// <summary>
/// A region switch in progress.
/// </summary>
/// <param name="WaitingForRun">Whether it's waiting for the VPN runs in progress to finish before restarting the tunnel.</param>
/// <param name="StartedTime">When the switch began, in UTC.</param>
public sealed record VpnSwitchProgress(string Region, bool WaitingForRun, DateTime StartedTime);
