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
/// <see cref="RequestSwitchAsync"/>, failovers a run asks for with <see cref="VpnRun.QueueFailover"/>, a first switch
/// when it has made none, so the region is always known, reapplying <see cref="VpnSettings.CurrentRegion"/> when
/// gluetun has lost it, which happens when gluetun restarts, and replacing it when
/// <see cref="VpnConfiguration.ServerRegions"/> no longer allows it. It's the only reader and writer of the
/// <see cref="VpnSettings"/> row.
/// </summary>
/// <remarks>
/// <para>
/// Runs and switches are kept apart by a <see cref="VpnGate"/> of its own: each <see cref="VpnRun"/> holds a share of
/// it, which any number of runs hold at once, and a switch holds it to itself until the new tunnel is up, so it waits
/// for the VPN runs in progress, and VPN runs that haven't started wait for it from when it's queued. A run reads its
/// region while holding its share, and a switch saves the new region before letting go, so the region a run records
/// is the one it ran in. A run whose known failure asks for a failover takes its <see cref="PendingFailover"/> before
/// it's saved as done and before it lets go of its <see cref="VpnRun"/>, which queues the failover's switch at once,
/// so no VPN run starts in the failed region; the failover itself runs later, outliving the lease.
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

    private enum SwitchOutcome
    {
        /// <summary>The VPN switched to another region.</summary>
        Switched,

        /// <summary>
        /// A failover found another switch had already left the failed region, so there was no need for one.
        /// </summary>
        AlreadyLeft,

        /// <summary>Every region a failover may pick was the current one or already failed in.</summary>
        NoRegionLeft,

        /// <summary>The switch failed, as <see cref="VpnSettings.LastError"/> says.</summary>
        Failed,
    }

    private readonly VpnGate _gate = new();

    // Requested regions, null for a random one. Only the latest matters; one made while another waits replaces it
    private readonly Channel<string?> _requests = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private volatile VpnSwitchProgress? _progress;

    // Failovers switch from the run worker's background, and the rest from this service's loop: one at a time, so each
    // picks its region from where the last one left the VPN, and only one shows its progress. A failover may be queued in
    // the gate while it waits here, which keeps new VPN runs out but doesn't hold up the switch ahead of it, since the
    // gate only goes to a switch that waits for it.
    private readonly SemaphoreSlim _switching = new(1, 1);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The switch in progress, or <see langword="null"/> if there's none.
    /// </summary>
    public VpnSwitchProgress? Progress => _progress;

    /// <summary>
    /// Whether a VPN run can't start now, since a switch is in progress or queued.
    /// </summary>
    public bool HoldsUpRuns => _gate.HoldsUpRuns;

    /// <summary>
    /// Raised when the last switch in progress or queued is done, so VPN runs held up by switches can start now.
    /// Raised on the thread that ended the switch; handlers must be quick and not throw.
    /// </summary>
    public event Action? RunsMayStart
    {
        add => _gate.RunsMayEnter += value;
        remove => _gate.RunsMayEnter -= value;
    }

    /// <summary>
    /// Lets a run of a watch with <c>UseVpn</c> use the VPN, unless a switch is in progress or queued. No switch
    /// starts until the run disposes the <see cref="VpnRun"/>, so the run stays in <see cref="VpnRun.Region"/>
    /// throughout.
    /// </summary>
    /// <returns>The run's lease, or <see langword="null"/> if it must wait for a switch.</returns>
    public async Task<VpnRun?> TryStartRunAsync(CancellationToken cancellationToken)
    {
        if (_gate.TryEnterRun() is not { } share)
        {
            return null;
        }

        try
        {
            // Read while holding the share, so no switch changes it during the run
            var region = configuration.IsEnabled ? await GetCurrentRegionAsync(cancellationToken) : null;
            return new VpnRun(this, share, region);
        }
        catch
        {
            share.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queues a failover from where <paramref name="run"/> ran, for <see cref="VpnRun.QueueFailover"/>.
    /// </summary>
    internal PendingFailover? QueueFailover(VpnRun run) =>
        configuration.IsEnabled ? new PendingFailover(this, _gate.QueueSwitch(), run.Region) : null;

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

    /// <summary>
    /// Fails the VPN over from <paramref name="failedRegion"/>, where a run's known failure asked for it, to a random
    /// region rotation may switch to that isn't one of <paramref name="failedRegions"/>, whether or not rotation is on.
    /// Waits for any other switch, then for the gate, then for the new tunnel. For <see cref="PendingFailover"/>.
    /// </summary>
    /// <param name="ticket">
    /// The failover's place in the gate's queue, taken before the run let go of its share, so no VPN run starts in the
    /// failed region meanwhile. The failover owns it from here.
    /// </param>
    /// <returns>Whether the VPN has left <paramref name="failedRegion"/>.</returns>
    internal async Task<bool> FailOverAsync(
        string? failedRegion,
        IReadOnlyCollection<string> failedRegions,
        VpnGate.SwitchTicket ticket,
        CancellationToken cancellationToken)
    {
        var outcome = await SwitchAsync(
            kind: SwitchKind.Failover,
            requestedRegion: null,
            cancellationToken: cancellationToken,
            failover: new FailoverRequest(failedRegion, failedRegions),
            queuedTicket: ticket);
        return outcome is SwitchOutcome.Switched or SwitchOutcome.AlreadyLeft;
    }

    private sealed record FailoverRequest(string? FailedRegion, IReadOnlyCollection<string> FailedRegions)
    {
        /// <summary>
        /// Whether the VPN is known to have left <see cref="FailedRegion"/>. A run before the Backend's first switch
        /// doesn't know its region, and the first switch may have landed in it, so the failover goes ahead.
        /// </summary>
        public bool HasLeft(string? currentRegion) =>
            FailedRegion is not null
            && !string.Equals(currentRegion, FailedRegion, StringComparison.OrdinalIgnoreCase);
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
    /// switched yet, makes its first switch, and tries again next time if that fails.
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
    /// <param name="failover">What a <see cref="SwitchKind.Failover"/> fails over from.</param>
    /// <param name="queuedTicket">The switch's place in <see cref="VpnGate"/>'s queue, if it took one already, which
    /// the switch owns from here. Otherwise the switch queues once it knows its region.</param>
    private async Task<SwitchOutcome> SwitchAsync(
        SwitchKind kind,
        string? requestedRegion,
        CancellationToken cancellationToken,
        FailoverRequest? failover = null,
        VpnGate.SwitchTicket? queuedTicket = null)
    {
        // Let go of once, when the tunnel is up or the switch ends without getting that far
        var ticket = queuedTicket;
        void LetGoOfGate()
        {
            ticket?.Dispose();
            ticket = null;
        }

        try
        {
            await _switching.WaitAsync(cancellationToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
                var gluetun = scope.ServiceProvider.GetRequiredService<GluetunHttpClient>();
                var settings = await dbContext.VpnSettings.SingleAsync(
                    s => s.Id == VpnSettings.SingletonId, cancellationToken);
                // Read after any earlier switch finished, and none can start before this one does
                if (failover is not null && failover.HasLeft(settings.CurrentRegion))
                {
                    LogAlreadyLeft(failover.FailedRegion);
                    return SwitchOutcome.AlreadyLeft;
                }

                string? region = null;
                var regionApplied = false;
                string? publicIp = null;
                string? error = null;
                // Whether the switch got as far as waiting for the gate: if it fails after that, it lets go before
                // saving the error, and before that it keeps a failover's place until the outcome is saved
                var waitedForGate = false;
                try
                {
                    region = requestedRegion
                        ?? await PickRegionAsync(kind, settings, failover, cancellationToken);
                    if (region is null)
                    {
                        LogNoFailoverRegion(failover!.FailedRegion);
                        return SwitchOutcome.NoRegionLeft;
                    }

                    // Once queued, no new VPN run starts, so it's queued before it shows as waiting for the ones in
                    // progress
                    ticket ??= _gate.QueueSwitch();
                    waitedForGate = true;
                    _progress = new VpnSwitchProgress(region, WaitingForRun: true, UtcNow);
                    await notifier.VpnChangedAsync();
                    await ticket.WaitAsync(cancellationToken);
                    _progress = _progress with { WaitingForRun = false };
                    await notifier.VpnChangedAsync();

                    LogSwitching(kind, region);
                    await gluetun.SetRegionAsync(region, cancellationToken);

                    regionApplied = true;
                    // Saved before letting go of the gate, so a run that starts after it records where it runs
                    await dbContext.VpnSettings
                        .Where(s => s.Id == VpnSettings.SingletonId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentRegion, region), CancellationToken.None);
                    publicIp = await WaitForTunnelAsync(
                        scope.ServiceProvider.GetRequiredService<VpnProbeHttpClient>(), cancellationToken);
                    LetGoOfGate();

                    if (publicIp is null)
                    {
                        error = $"Switched to {region}, but the tunnel wasn't back up after "
                            + $"{TunnelUpTimeout.TotalMinutes:0} minutes.";
                    }
                }
                catch (Exception ex)
                    when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    if (waitedForGate)
                    {
                        LetGoOfGate();
                    }

                    error = ex.Message;
                }
                finally
                {
                    _progress = null;
                }

                return await RecordOutcomeAsync(
                    kind, dbContext, settings, regionApplied ? region : null, publicIp, error);
            }
            finally
            {
                _switching.Release();
            }
        }
        finally
        {
            LetGoOfGate();
        }
    }

    /// <summary>
    /// Saves how a switch that got as far as picking a region went.
    /// </summary>
    /// <param name="appliedRegion">The region gluetun was switched to, if it got that far.</param>
    private async Task<SwitchOutcome> RecordOutcomeAsync(
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

        settings.LastError = error;
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await notifier.VpnChangedAsync();

        if (error is null)
        {
            LogSwitched(appliedRegion!, publicIp);
            return SwitchOutcome.Switched;
        }

        LogSwitchFailed(kind, error);
        return SwitchOutcome.Failed;
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
        FailoverRequest? failover,
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

        var rotationRegion = settings.PickRotationRegion(regions, Random.Shared)
            ?? throw new InvalidOperationException(DescribeNoRotationRegion(kind, settings, regions));
        // Running out of regions is how a streak in every region ends, not something wrong with the VPN, so only the
        // settings leaving no region at all count as an error
        return failover is null
            ? rotationRegion
            : settings.PickFailoverRegion(regions, failover.FailedRegions, Random.Shared);
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
            SwitchKind.Failover => "There are no regions to fail over to",
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
