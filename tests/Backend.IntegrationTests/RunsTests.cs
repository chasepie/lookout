using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>Admitting Runs and Test runs, sharing Run slots, and knowing which Watches are Running, through <see cref="Runs"/>.</summary>
[TestClass]
public sealed class RunsTests(TestContext testContext)
{
    [TestMethod]
    public async Task RecordsARunAndFinishesWithIt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        var runs = factory.Services.GetRequiredService<Runs>();
        var watchId = await AddWatchAsync(factory);

        var admission = runs.Run(watchId);

        var execution = await Assert.IsInstanceOfType<Admitted>(admission).Finished;
        Assert.AreEqual(RunExecutionStatus.Recorded, execution.Status);
        Assert.AreEqual(watchId, execution.Run!.WatchId);
        Assert.AreEqual("£20", execution.Run.Content);
    }

    [TestMethod]
    public async Task AWatchIsRunningUntilItsRunIsRecordedAndRefusesAnotherMeanwhile()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = factory.Services.GetRequiredService<Runs>();
        var watchId = await AddWatchAsync(factory);

        var finished = Assert.IsInstanceOfType<Admitted>(runs.Run(watchId)).Finished;
        await runner.WaitForStarted(1);
        var second = runs.Run(watchId);
        var duringRun = runs.IsRunning(watchId);
        runner.FinishAll();
        await finished;

        Assert.AreEqual(new Refused(RunRefusal.Executing, Gated: false), second);
        Assert.IsTrue(duringRun);
        Assert.IsFalse(runs.IsRunning(watchId));
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task AWatchIsRunningUntilItsRunsNotificationsAreSent()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Channels.OnSend = (_, _) =>
        {
            sending.TrySetResult();
            return sent.Task;
        };
        var runs = factory.Services.GetRequiredService<Runs>();
        var watchId = await AddWatchAsync(factory, baseline: "£19", notifies: true);

        var finished = Assert.IsInstanceOfType<Admitted>(runs.Run(watchId)).Finished;
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);
        var whileSending = runs.Run(watchId);
        var runningWhileSending = runs.IsRunning(watchId);
        sent.SetResult();
        var execution = await finished;

        Assert.AreEqual(new Refused(RunRefusal.Executing, Gated: false), whileSending);
        Assert.IsTrue(runningWhileSending);
        Assert.IsTrue(execution.Run!.IsChange);
        Assert.HasCount(1, factory.Channels.Sent);
        Assert.IsFalse(runs.IsRunning(watchId));
    }

    [TestMethod]
    public async Task AWatchWaitingForARunSlotIsRunningAndRefusesAnotherRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var busy = await AddWatchAsync(host);
        var waiting = await AddWatchAsync(host);
        runs.Run(busy);
        await runner.WaitForStarted(1);

        var admitted = Assert.IsInstanceOfType<Admitted>(runs.Run(waiting, (_, _) => Task.FromResult(true)));

        Assert.IsTrue(runs.IsRunning(waiting));
        Assert.AreEqual(new Refused(RunRefusal.WaitingForSlot, Gated: true), runs.Run(waiting));
        runner.FinishOne();
        await runner.WaitForStarted(2);
        runner.FinishAll();
        Assert.AreEqual(RunExecutionStatus.Recorded, (await admitted.Finished).Status);
    }

    [TestMethod]
    public async Task HandsOutRunSlotsInTheOrderRunsWereAdmitted()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var watches = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            watches.Add(await AddWatchAsync(host, notificationUrl: $"https://example.com/{index}"));
        }

        var finished = watches.Select(watchId => Assert.IsInstanceOfType<Admitted>(runs.Run(watchId)).Finished).ToList();
        for (var started = 1; started <= watches.Count; started++)
        {
            await runner.WaitForStarted(started);
            runner.FinishOne();
        }
        await Task.WhenAll(finished);

        Assert.AreEqual("https://example.com/0 https://example.com/1 https://example.com/2 https://example.com/3",
            string.Join(' ', runner.Requests.Select(request => request.Context.NotificationUrl)));
    }

    [TestMethod]
    public async Task ATestRunWaitsForARunSlotButNeverMakesTheWatchRunning()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var busy = await AddWatchAsync(host);
        var tested = await AddWatchAsync(host);
        runs.Run(busy);
        await runner.WaitForStarted(1);

        var testing = runs.TestAsync(tested, "export default {}", "ts", testContext.CancellationToken);
        var runningWhileWaiting = runs.IsRunning(tested);
        runner.FinishOne();
        await runner.WaitForStarted(2);
        var runningWhileTesting = runs.IsRunning(tested);
        runner.FinishOne();
        var testRun = await testing;

        Assert.AreEqual(RunOutcome.Succeeded, testRun!.Outcome);
        Assert.IsTrue(runner.Requests[1].AlwaysScreenshot);
        Assert.IsFalse(runningWhileWaiting);
        Assert.IsFalse(runningWhileTesting);
    }

    [TestMethod]
    public async Task ATestRunMayExecuteAlongsideARunOfTheSameWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = factory.Services.GetRequiredService<Runs>();
        var watchId = await AddWatchAsync(factory);
        var finished = Assert.IsInstanceOfType<Admitted>(runs.Run(watchId)).Finished;
        await runner.WaitForStarted(1);

        var testing = runs.TestAsync(watchId, "export default {}", "ts", testContext.CancellationToken);
        await runner.WaitForStarted(2);
        runner.FinishAll();

        Assert.AreEqual(RunOutcome.Succeeded, (await testing)!.Outcome);
        Assert.AreEqual(RunExecutionStatus.Recorded, (await finished).Status);
    }

    [TestMethod]
    public async Task ATestRunOfAWatchThatDoesNotExistIsNull()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runs = factory.Services.GetRequiredService<Runs>();

        Assert.IsNull(await runs.TestAsync(Guid.NewGuid(), "export default {}", "ts", testContext.CancellationToken));
    }

    [TestMethod]
    public async Task AGateIsAskedOnceTheRunHasASlotAndCanTurnItDown()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        // Sets up the account the listener logs in to.
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(host, testContext.CancellationToken);
        var busy = await AddWatchAsync(host);
        var gated = await AddWatchAsync(host);
        runs.Run(busy);
        await runner.WaitForStarted(1);
        var asked = 0;

        var admitted = Assert.IsInstanceOfType<Admitted>(runs.Run(gated, (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(false);
        }));
        var beforeSlot = Volatile.Read(ref asked);
        runner.FinishOne();
        var execution = await admitted.Finished;

        Assert.AreEqual(0, beforeSlot);
        Assert.AreEqual(1, asked);
        Assert.AreEqual(RunExecutionStatus.NotClaimed, execution.Status);
        Assert.AreEqual(1, runner.Started);
        Assert.IsFalse(runs.IsRunning(gated));
        var events = (await listener.NextAsync(5, testContext.CancellationToken))
            .Where(item => item.Payload.GetProperty("watchId").GetString() == gated.ToString()).ToList();
        Assert.AreEqual("RunStarted RunFinished", string.Join(' ', events.Select(item => item.Name)));
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, events[1].Payload.GetProperty("runId").ValueKind);
    }

    [TestMethod]
    public async Task AVpnRunWaitingForTheVpnLendsItsSlotAndTakesTheNextOneFirst()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        factory.Vpn.Status = VpnStatus.Disconnected();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var vpnWatch = await AddWatchAsync(host, usesVpn: true);
        var direct = await AddWatchAsync(host);
        var queued = await AddWatchAsync(host);

        var vpnRun = Assert.IsInstanceOfType<Admitted>(runs.Run(vpnWatch)).Finished;
        // Only starts once the VPN Run has given its slot back.
        runs.Run(direct);
        await runner.WaitForStarted(1);
        runs.Run(queued);
        factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
        var readsBefore = factory.Vpn.StatusReads;
        await AdvanceUntil(factory, () => factory.Vpn.StatusReads > readsBefore);
        runner.FinishOne();
        await runner.WaitForStarted(2);
        runner.FinishAll();
        await runner.WaitForStarted(3);
        runner.FinishAll();

        Assert.AreEqual(new ProxySettings("http://gluetun:8888"), runner.Requests[1].Proxy);
        Assert.AreEqual("203.0.113.7", (await vpnRun).Run!.VpnExitIp);
    }

    [TestMethod]
    public async Task AVpnWatchsTestRunChecksTheVpnOnceItHasARunSlot()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var direct = await AddWatchAsync(host);
        var vpnWatch = await AddWatchAsync(host, usesVpn: true);
        runs.Run(direct);
        await runner.WaitForStarted(1);

        var testing = runs.TestAsync(vpnWatch, "export default {}", "ts", testContext.CancellationToken);
        // Long enough for a Test run that checked the VPN before waiting for the slot to have done so already.
        await Task.Delay(300, testContext.CancellationToken);
        factory.Vpn.Status = VpnStatus.Disconnected();
        runner.FinishOne();
        var testRun = await testing;

        Assert.AreEqual(1, runner.Started);
        Assert.AreEqual("The VPN is not connected, so this Test run was not started.", testRun!.ErrorMessage);
    }

    [TestMethod]
    public async Task StoppingCancelsRunsInProgressAndThoseWaitingForASlot()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = OneRunSlot(factory);
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        var runs = host.Services.GetRequiredService<Runs>();
        var executing = Assert.IsInstanceOfType<Admitted>(runs.Run(await AddWatchAsync(host))).Finished;
        await runner.WaitForStarted(1);
        var waiting = Assert.IsInstanceOfType<Admitted>(runs.Run(await AddWatchAsync(host))).Finished;

        await host.DisposeAsync();

        Assert.IsTrue(executing.IsCanceled);
        Assert.IsTrue(waiting.IsCanceled);
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task StoppingGivesUpOnARunStillSendingWhenTheShutdownTimeoutPasses()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1))));
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Channels.OnSend = (_, _) =>
        {
            sending.TrySetResult();
            return sent.Task;
        };
        var runs = host.Services.GetRequiredService<Runs>();
        var finished = Assert.IsInstanceOfType<Admitted>(runs.Run(await AddWatchAsync(host, baseline: "£19", notifies: true))).Finished;
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        try
        {
            // Notifications of a finished Run go out despite stopping, so this one outlasts the shutdown timeout.
            await host.DisposeAsync();
        }
        finally
        {
            sent.SetResult();
        }

        Assert.AreEqual(RunExecutionStatus.Recorded, (await finished).Status);
    }

    private static WebApplicationFactory<Program> OneRunSlot(LookoutWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:MaxConcurrentRuns", "1"));

    /// <summary>Moves fake time on in small steps, letting Runs react to each, until <paramref name="done"/> holds.</summary>
    private async Task AdvanceUntil(LookoutWebApplicationFactory factory, Func<bool> done)
    {
        for (var step = 0; !done(); step++)
        {
            Assert.IsLessThan(1000, step, "The Run never got there.");
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, testContext.CancellationToken);
        }
    }

    /// <summary>Adds a Watch that only runs when a test asks, as the scheduler never finds it due.</summary>
    private static async Task<Guid> AddWatchAsync(WebApplicationFactory<Program> factory, bool usesVpn = false,
        string notificationUrl = "https://example.com/widget", string? baseline = null, bool notifies = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var watch = new Watch
        {
            Name = "Widget price",
            NotificationUrl = notificationUrl,
            Schedule = new Schedule { Kind = ScheduleKind.Interval, Every = 1, Unit = IntervalUnit.Hours },
            UsesVpn = usesVpn,
            Baseline = baseline
        };
        if (notifies)
        {
            watch.Channels.Add(new Channel { Name = "Phone", Kind = ChannelKind.Discord, WebhookUrl = "https://discord.example/hook" });
        }
        var version = new ScriptVersion { WatchId = watch.Id, Number = 1, Source = "export default {}", FileKind = "ts" };
        watch.ScriptVersions.Add(version);
        database.Watches.Add(watch);
        await database.SaveChangesAsync();
        watch.CurrentScriptVersionId = version.Id;
        await database.SaveChangesAsync();
        return watch.Id;
    }
}
