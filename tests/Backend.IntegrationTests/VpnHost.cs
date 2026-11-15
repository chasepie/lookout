using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The Backend with the VPN set up against a <see cref="FakeGluetun"/> and its clock faked, for calling
/// <see cref="Backend.VpnServices.Vpn"/> directly. Its loop runs for real, on the fake clock, which only moves when a
/// test moves it.
/// </summary>
internal sealed class VpnHost : IAsyncDisposable
{
    /// <summary>
    /// Where the fake clock starts.
    /// </summary>
    public static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    // Real time between steps of the fake clock, so the loop keeps up
    private static readonly TimeSpan StepPause = TimeSpan.FromMilliseconds(10);

    // Real time for the loop to act on the last step, were it going to
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(30);

    private readonly LookoutFactory _factory;
    private readonly string _dataDirectory;

    private VpnHost(LookoutFactory factory, FakeGluetun gluetun, FakeTimeProvider time, string dataDirectory)
    {
        _factory = factory;
        _dataDirectory = dataDirectory;
        Gluetun = gluetun;
        Time = time;
        Vpn = factory.Services.GetRequiredService<Vpn>();
    }

    public Vpn Vpn { get; }

    public FakeGluetun Gluetun { get; }

    public FakeTimeProvider Time { get; }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Stores the settings <paramref name="seed"/> makes, as if from before a restart, then starts the Backend with
    /// gluetun offering <paramref name="servers"/>, and returns once the VPN has checked gluetun's region.
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
        var factory = new LookoutFactory(
            dataDirectory: dataDirectory,
            settings: FakeGluetun.Settings(dataDirectory, serverRegions ?? string.Join(',', servers), servers),
            configureServices: services =>
            {
                gluetun.Register(services);
                services.AddSingleton<TimeProvider>(time);
            });
        var host = new VpnHost(factory, gluetun, time, dataDirectory);
        // The loop's first pass checks gluetun's region at once. A switch a test makes before then could land between
        // that check reading the VPN's region and asking gluetun for its own, which would undo it
        await host.WaitUntilAsync(() => Task.FromResult(gluetun.SettingsReads > 0), cancellationToken);
        return host;
    }

    public async Task<VpnSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        (await Vpn.GetStatusAsync(cancellationToken)).Settings!;

    /// <summary>
    /// Starts a VPN run, once no switch holds runs up, moving the clock on until then.
    /// </summary>
    public async Task<VpnRun> StartRunAsync(CancellationToken cancellationToken)
    {
        VpnRun? run = null;
        await AdvanceUntilAsync(async () => (run = await Vpn.TryStartRunAsync(cancellationToken)) is not null,
            cancellationToken);
        return run!;
    }

    /// <summary>
    /// Runs <paramref name="failover"/>, moving the clock on until it's done.
    /// </summary>
    public async Task<bool> FailOverAsync(
        PendingFailover failover, IReadOnlyCollection<string> failedRegions, CancellationToken cancellationToken)
    {
        var failingOver = failover.FailOverAsync(failedRegions, cancellationToken);
        await AdvanceUntilAsync(() => failingOver.IsCompleted, cancellationToken);
        return await failingOver;
    }

    /// <summary>
    /// Waits for <paramref name="condition"/> without moving the clock, for what the loop does at once.
    /// </summary>
    public Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken) =>
        PollAsync(condition, advance: false, cancellationToken);

    /// <summary>
    /// Moves the clock on a second at a time until <paramref name="condition"/> holds.
    /// </summary>
    public Task AdvanceUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken) =>
        PollAsync(condition, advance: true, cancellationToken);

    public Task AdvanceUntilAsync(Func<bool> condition, CancellationToken cancellationToken) =>
        AdvanceUntilAsync(() => Task.FromResult(condition()), cancellationToken);

    /// <summary>
    /// Moves the clock on by <paramref name="duration"/>, a second at a time, then gives the loop a moment to act on
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
