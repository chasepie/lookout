using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The Backend with the VPN set up against a <see cref="FakeGluetun"/>, the Runner faked at its HTTP boundary, and its
/// clock faked, for calling <see cref="Backend.VpnServices.Vpn"/> directly and running watches through the API. Its run
/// worker runs for real, polling in real time, but goes by the fake clock, which only moves when a test moves it, for
/// when switches are due and how long they wait for the tunnel.
/// </summary>
internal sealed class VpnHost : IAsyncDisposable
{
    /// <summary>
    /// Where the fake clock starts.
    /// </summary>
    public static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    // Real time between steps of the fake clock, so the run worker keeps up
    private static readonly TimeSpan StepPause = TimeSpan.FromMilliseconds(10);

    // Real time for the run worker to act on the last step, were it going to: longer than it waits between turns, which
    // goes by real time
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(30);

    private static readonly object Success = new { success = true, content = "In stock" };

    private readonly LookoutFactory _factory;
    private readonly string _dataDirectory;

    private VpnHost(
        LookoutFactory factory, FakeGluetun gluetun, FakeRunner runner, FakeTimeProvider time, string dataDirectory)
    {
        _factory = factory;
        _dataDirectory = dataDirectory;
        Gluetun = gluetun;
        Runner = runner;
        Time = time;
        Vpn = factory.Services.GetRequiredService<Vpn>();
    }

    public Vpn Vpn { get; }

    public FakeGluetun Gluetun { get; }

    /// <summary>
    /// The Runner, whose runs succeed unless a test says otherwise.
    /// </summary>
    public FakeRunner Runner { get; }

    /// <summary>
    /// A client signed in as the Owner.
    /// </summary>
    public HttpClient Client { get; private set; } = null!;

    public FakeTimeProvider Time { get; }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Stores the settings <paramref name="seed"/> makes, as if from before a restart, then starts the Backend with
    /// gluetun offering <paramref name="servers"/>, and returns once the VPN has asked gluetun for its region. Up to
    /// three runs run at once, so VPN runs can overlap.
    /// </summary>
    /// <param name="serverRegions">
    /// <c>GLUETUN_SERVER_REGIONS</c>; every one of <paramref name="servers"/> if not given.
    /// </param>
    public static async Task<VpnHost> StartAsync(
        FakeGluetun gluetun,
        string[] servers,
        Action<VpnSettings> seed,
        CancellationToken cancellationToken,
        string? serverRegions = null)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("lookout-tests-").FullName;
        await using (var before = new LookoutFactory(dataDirectory: dataDirectory))
        {
            using var scope = before.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var settings = await dbContext.VpnSettings.SingleAsync(
                s => s.Id == VpnSettings.SingletonId, cancellationToken);
            seed(settings);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var time = new FakeTimeProvider(new DateTimeOffset(Start));
        var runner = new FakeRunner(Success);
        var environment = FakeGluetun.Settings(dataDirectory, serverRegions ?? string.Join(',', servers), servers);
        environment[EnvVars.LookoutMaxConcurrentRuns] = "3";
        var factory = new LookoutFactory(
            dataDirectory: dataDirectory,
            settings: environment,
            configureServices: services =>
            {
                gluetun.Register(services);
                services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner);
                services.AddSingleton<TimeProvider>(time);
                // Sessions keep the real clock, since the client drops a cookie that expired by it
                services.PostConfigureAll<CookieAuthenticationOptions>(options => options.TimeProvider = TimeProvider.System);
            });
        var host = new VpnHost(factory, gluetun, runner, time, dataDirectory);
        host.Client = await factory.CreateSignedInClientAsync(cancellationToken);
        // The worker's first turn checks gluetun's region at once, so tests start once it has asked gluetun, rather than
        // racing it. A first switch that check plans may still be under way, for tests to wait for
        await host.WaitUntilAsync(() => gluetun.SettingsReads > 0, cancellationToken);
        return host;
    }

    public async Task<VpnSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        (await Vpn.GetStatusAsync(cancellationToken)).Settings!;

    /// <summary>
    /// Adds a watch with <c>UseVpn</c> that avoids <paramref name="regions"/>.
    /// </summary>
    /// <returns>The watch's ID.</returns>
    public async Task<int> AddVpnWatchAsync(CancellationToken cancellationToken, params string[] regions)
    {
        var watchId = await Client.CreateWatchAsync("Tickets", cancellationToken, ("UseVpn", "true"));
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        foreach (var region in regions)
        {
            dbContext.AvoidedRegions.Add(new AvoidedRegion { WatchId = watchId, Region = region, CreatedTime = Now });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return watchId;
    }

    /// <summary>
    /// Adds a watch without <c>UseVpn</c>.
    /// </summary>
    /// <returns>The watch's ID.</returns>
    public Task<int> AddWatchWithoutVpnAsync(CancellationToken cancellationToken) =>
        Client.CreateWatchAsync("Prices", cancellationToken);

    /// <summary>
    /// Turns the watch's <c>UseVpn</c> off, as the Owner would.
    /// </summary>
    public async Task TurnVpnOffAsync(int watchId, CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        await dbContext.Watches
            .Where(w => w.Id == watchId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.UseVpn, false), cancellationToken);
    }

    /// <summary>
    /// Clears every region the watch avoids, as the Owner would.
    /// </summary>
    public async Task ClearAvoidedRegionsAsync(int watchId, CancellationToken cancellationToken)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        await dbContext.AvoidedRegions.Where(a => a.WatchId == watchId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Queues a run of the watch, and returns it once it's done, moving the clock on until then, for a run that waits
    /// for a switch, which waits for the tunnel on the clock.
    /// </summary>
    public async Task<WatchRunSummary> RunAsync(int watchId, CancellationToken cancellationToken)
    {
        var before = (await Client.GetRunsAsync(watchId, cancellationToken)).Count;
        await Client.QueueRunAsync(watchId, cancellationToken);
        return await WaitForRunAsync(watchId, before, cancellationToken);
    }

    /// <summary>
    /// The watch's run at <paramref name="index"/>, oldest first, once it's done, moving the clock on until then.
    /// </summary>
    public async Task<WatchRunSummary> WaitForRunAsync(int watchId, int index, CancellationToken cancellationToken)
    {
        WatchRunSummary? run = null;
        await AdvanceUntilAsync(async () =>
            (run = (await Client.GetRunsAsync(watchId, cancellationToken)).ElementAtOrDefault(index))
            is { Status: WatchRunStatus.Done }, cancellationToken);
        return run!;
    }

    /// <summary>
    /// The watch's only run, without waiting for it.
    /// </summary>
    public async Task<WatchRunSummary> GetRunAsync(int watchId, CancellationToken cancellationToken) =>
        (await Client.GetRunsAsync(watchId, cancellationToken)).Single();

    /// <summary>
    /// Waits for <paramref name="condition"/> without moving the clock, for what the run worker does at once.
    /// </summary>
    public Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken) =>
        PollAsync(condition, advance: false, cancellationToken);

    public Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken) =>
        WaitUntilAsync(() => Task.FromResult(condition()), cancellationToken);

    /// <summary>
    /// Moves the clock on a second at a time until <paramref name="condition"/> holds.
    /// </summary>
    public Task AdvanceUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken) =>
        PollAsync(condition, advance: true, cancellationToken);

    public Task AdvanceUntilAsync(Func<bool> condition, CancellationToken cancellationToken) =>
        AdvanceUntilAsync(() => Task.FromResult(condition()), cancellationToken);

    /// <summary>
    /// Moves the clock on by <paramref name="duration"/>, a second at a time, then gives the run worker a moment to act on
    /// it, for checking that something didn't happen by then.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        for (var moved = TimeSpan.Zero; moved < duration; moved += Step)
        {
            Time.Advance(Step < duration - moved ? Step : duration - moved);
            await Task.Delay(StepPause, cancellationToken);
        }

        await Task.Delay(SettleTime, cancellationToken);
    }

    private async Task PollAsync(Func<Task<bool>> condition, bool advance, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The condition didn't hold within {RealTimeout.TotalSeconds:0} seconds.");
            }

            if (advance)
            {
                Time.Advance(Step);
            }

            await Task.Delay(StepPause, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite can still hold the file briefly; it's a temp directory
        }
    }
}
