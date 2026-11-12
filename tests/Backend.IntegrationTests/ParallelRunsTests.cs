using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Controllers;
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
            await QueueRunAsync(client, watchId);
        }

        await WaitUntilAsync(() => Task.FromResult(runner.InProgress == 2));
        await Task.Delay(WorkerSettleTime, Ct);
        Assert.AreEqual(2, runner.InProgress);
        var third = (await GetRunsAsync(client, watchIds[2])).Single();
        Assert.AreEqual(WatchRunStatus.Created, third.Status, "The oldest runs go first, and the third waits for a slot.");

        hold.SetResult();
        foreach (var watchId in watchIds)
        {
            await WaitForRunsAsync(client, watchId, 1);
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
            await QueueRunAsync(client, watchId);
        }

        await WaitUntilAsync(() => Task.FromResult(runner.InProgress == 1));
        await Task.Delay(WorkerSettleTime, Ct);
        Assert.AreEqual(1, runner.InProgress);

        hold.SetResult();
        var runs = await WaitForRunsAsync(client, watchId, 3);

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

        await QueueRunAsync(client, busyId);
        await WaitUntilAsync(() => Task.FromResult(runner.InProgress == 1));
        await QueueRunAsync(client, busyId);
        await QueueRunAsync(client, otherId);
        var other = (await WaitForRunsAsync(client, otherId, 1)).Single();

        var busy = await GetRunsAsync(client, busyId);
        Assert.AreEqual(WatchRunStatus.Running, busy[0].Status);
        Assert.AreEqual(WatchRunStatus.Created, busy[1].Status);
        Assert.IsLessThan(busy[1].Id, busy[0].Id);
        Assert.IsGreaterThan(busy[1].Id, other.Id);

        hold.SetResult();
        await WaitForRunsAsync(client, busyId, 2);
    }

    private static LookoutFactory CreateFactory(FakeRunner runner, int maxConcurrentRuns) => new(
        settings: new Dictionary<string, string?>
        {
            [EnvVars.LookoutMaxConcurrentRuns] = maxConcurrentRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        configureServices: services =>
            services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner));

    private async Task QueueRunAsync(HttpClient client, int watchId)
    {
        using var response = await client.PostAsync($"/api/Watches/{watchId}/runs", null, Ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    /// <summary>
    /// Waits for the watch to have <paramref name="count"/> runs, all done, and returns them oldest first.
    /// </summary>
    private async Task<List<WatchRunSummary>> WaitForRunsAsync(HttpClient client, int watchId, int count)
    {
        List<WatchRunSummary> runs = [];
        await WaitUntilAsync(async () =>
        {
            runs = await GetRunsAsync(client, watchId);
            return runs.Count >= count && runs.All(r => r.Status == WatchRunStatus.Done);
        });
        return runs;
    }

    private async Task<List<WatchRunSummary>> GetRunsAsync(HttpClient client, int watchId)
    {
        var newestFirst = await client.GetFromJsonAsync<List<WatchRunSummary>>($"/api/Watches/{watchId}/runs", Ct);
        return [.. newestFirst!.OrderBy(r => r.Id)];
    }
}
