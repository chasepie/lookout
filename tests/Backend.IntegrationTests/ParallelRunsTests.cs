using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Which runs the worker runs at once, with the Runner faked at its HTTP boundary. How VPN runs and region switches wait
/// for each other is in <see cref="VpnFailoverTests"/>.
/// </summary>
[TestClass]
public sealed class ParallelRunsTests
{
    private static readonly object Success = new { success = true, content = "In stock" };

    // Long enough for the worker to have started a run, were it going to
    private static readonly TimeSpan WorkerSettleTime = TimeSpan.FromSeconds(2);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task RunsOfDifferentWatchesRunAtOnceUpToTheLimit()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(Success) { WhileRunning = () => hold.Task };
        await using var factory = CreateFactory(runner, maxConcurrentRuns: 2);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        int[] watchIds =
        [
            await client.CreateWatchAsync("First", Ct),
            await client.CreateWatchAsync("Second", Ct),
            await client.CreateWatchAsync("Third", Ct),
        ];

        foreach (var watchId in watchIds)
        {
            await client.QueueRunAsync(watchId, Ct);
        }

        await LookoutApi.WaitUntilAsync(() => Task.FromResult(runner.InProgress == 2), Ct);
        await Task.Delay(WorkerSettleTime, Ct);
        Assert.AreEqual(2, runner.InProgress);
        var third = (await client.GetRunsAsync(watchIds[2], Ct)).Single();
        Assert.AreEqual(WatchRunStatus.Created, third.Status, "The oldest runs go first, and the third waits for a slot.");

        hold.SetResult();
        foreach (var watchId in watchIds)
        {
            await client.WaitForRunsAsync(watchId, 1, Ct);
        }

        Assert.AreEqual(2, runner.MaxInProgress);
    }

    [TestMethod]
    public async Task AWatchsRunsRunOneAtATimeInOrder()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(Success) { WhileRunning = () => hold.Task };
        await using var factory = CreateFactory(runner, maxConcurrentRuns: 3);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await client.CreateWatchAsync("Tickets", Ct);

        for (var i = 0; i < 3; i++)
        {
            await client.QueueRunAsync(watchId, Ct);
        }

        await LookoutApi.WaitUntilAsync(() => Task.FromResult(runner.InProgress == 1), Ct);
        await Task.Delay(WorkerSettleTime, Ct);
        Assert.AreEqual(1, runner.InProgress);

        hold.SetResult();
        var runs = await client.WaitForRunsAsync(watchId, 3, Ct);

        Assert.AreEqual(1, runner.MaxInProgress);
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(runs[i - 1].EndTime!.Value, runs[i].StartTime!.Value);
        }
    }

    [TestMethod]
    public async Task AWatchWaitingForItsRunDoesntHoldUpALaterRunOfAnother()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // Only the first run is held
        var runner = new FakeRunner(Success)
        {
            WhileRunning = () => Interlocked.Increment(ref calls) == 1 ? hold.Task : Task.CompletedTask,
        };
        await using var factory = CreateFactory(runner, maxConcurrentRuns: 3);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var busyId = await client.CreateWatchAsync("Busy", Ct);
        var otherId = await client.CreateWatchAsync("Other", Ct);

        await client.QueueRunAsync(busyId, Ct);
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(runner.InProgress == 1), Ct);
        await client.QueueRunAsync(busyId, Ct);
        await client.QueueRunAsync(otherId, Ct);
        var other = (await client.WaitForRunsAsync(otherId, 1, Ct)).Single();

        var busy = await client.GetRunsAsync(busyId, Ct);
        Assert.AreEqual(WatchRunStatus.Running, busy[0].Status);
        Assert.AreEqual(WatchRunStatus.Created, busy[1].Status);
        Assert.IsLessThan(busy[1].Id, busy[0].Id);
        Assert.IsGreaterThan(busy[1].Id, other.Id);

        hold.SetResult();
        await client.WaitForRunsAsync(busyId, 2, Ct);
    }

    private static LookoutFactory CreateFactory(FakeRunner runner, int maxConcurrentRuns) => new(
        settings: new Dictionary<string, string?>
        {
            [EnvVars.LookoutMaxConcurrentRuns] = maxConcurrentRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        configureServices: services =>
            services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner));
}
