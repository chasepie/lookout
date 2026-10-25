using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class SchedulingTests(TestContext testContext)
{
    private static readonly RunnerResult Succeeded = BlockingRunner.Succeeded;

    [TestMethod]
    public async Task PollsForDueWatches()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();

        factory.Time.Advance(TimeSpan.FromMinutes(5));

        await WaitForHistory(client, id, 1);
    }

    [TestMethod]
    public async Task RunsAWatchWhenItIsDue()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = factory.Time.GetUtcNow();
        var watch = await CreateWatch(client, Every(5, "minutes"));
        var id = watch["id"]!.GetValue<string>();
        Assert.AreEqual(created.AddMinutes(5), watch["nextRunAt"]!.GetValue<DateTimeOffset>());

        factory.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.IsEmpty(await StartDueRuns(factory));

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await StartAndFinishDueRuns(factory);
        var history = await History(client, id);

        Assert.AreEqual("succeeded", history[0]!["outcome"]!.GetValue<string>());
        Assert.AreEqual(created.AddMinutes(5), history[0]!["startedAt"]!.GetValue<DateTimeOffset>());
        var listed = await Watch(client, id);
        Assert.AreEqual("£20", listed["baseline"]!.GetValue<string>());
        Assert.AreEqual(created.AddMinutes(10), listed["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task APausedWatchIsNotScheduled()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();

        await PostWatchAction(client, id, "pause");
        var paused = await Watch(client, id);
        factory.Time.Advance(TimeSpan.FromHours(1));

        Assert.IsEmpty(await StartDueRuns(factory));
        Assert.IsNull(paused["nextRunAt"]);
        Assert.IsEmpty(await History(client, id));
        Assert.IsNull((await Watch(client, id))["nextRunAt"]);
    }

    [TestMethod]
    public async Task ResumingSchedulesTheNextRunFromNow()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        await PostWatchAction(client, id, "pause");
        factory.Time.Advance(TimeSpan.FromHours(1));

        await PostWatchAction(client, id, "resume");
        var resumed = await Watch(client, id);

        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), resumed["nextRunAt"]!.GetValue<DateTimeOffset>());
        Assert.IsEmpty(await StartDueRuns(factory));
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await StartAndFinishDueRuns(factory);
        Assert.HasCount(1, await History(client, id));
    }

    [TestMethod]
    public async Task APausedWatchStaysUnscheduledAfterARestart()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            await using (var before = SharingDatabase(factory, databasePath))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
                await PostWatchAction(client, id, "pause");
            }

            factory.Time.Advance(TimeSpan.FromHours(1));
            await using var after = SharingDatabase(factory, databasePath);
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);
            factory.Time.Advance(TimeSpan.FromHours(1));

            Assert.IsEmpty(await StartDueRuns(after));
            Assert.IsNull((await Watch(restarted, id))["nextRunAt"]);
            Assert.IsEmpty(await History(restarted, id));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task ChangingAPausedWatchsScheduleLeavesItUnscheduled()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        await PostWatchAction(client, id, "pause");

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule", Every(10, "minutes"), testContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var changed = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual(10, changed!["schedule"]!["every"]!.GetValue<int>());
        Assert.IsNull(changed["nextRunAt"]);
        await PostWatchAction(client, id, "resume");
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(10),
            (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task ResumingUsesAScheduleChangedWhileItResumes()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        HttpClient? client = null;
        string? id = null;
        var changeSchedule = new BeforeCommand("AND \"w\".\"IsPaused\"", async () =>
        {
            var response = await client!.PutAsJsonAsync($"/api/watches/{id}/schedule", Every(10, "minutes"), testContext.CancellationToken);
            response.EnsureSuccessStatusCode();
        });
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<LookoutDbContext>(options => options.AddInterceptors(changeSchedule))));
        client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        id = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
        await PostWatchAction(client, id, "pause");

        await PostWatchAction(client, id, "resume");

        Assert.IsTrue(changeSchedule.Fired);
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(10),
            (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        client.Dispose();
    }

    [TestMethod]
    public async Task RunsOnceAfterTimePassesSeveralMissedRunsThenSchedulesFromNow()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, Every(5, "minutes"));
        var id = watch["id"]!.GetValue<string>();

        factory.Time.Advance(TimeSpan.FromHours(1));
        await StartAndFinishDueRuns(factory);

        Assert.IsEmpty(await StartDueRuns(factory));
        Assert.HasCount(1, await History(client, id));
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task SkipsRunsMissedWhileLookoutWasDown()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            await using (var before = SharingDatabase(factory, databasePath))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
            }

            factory.Time.Advance(TimeSpan.FromHours(1));
            await using var after = SharingDatabase(factory, databasePath);
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.IsEmpty(await StartDueRuns(after));
            Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(restarted, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
            factory.Time.Advance(TimeSpan.FromMinutes(5));
            await StartAndFinishDueRuns(after);
            Assert.HasCount(1, await History(restarted, id));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    // It is 12:00 UTC on 19 October 2026: 21:00 in Tokyo (UTC+9) and 08:00 in New York (UTC-4, daylight saving).
    [DataRow(null, "daily", "09:00", null, "2026-10-20T09:00:00Z")]
    [DataRow(null, "daily", "13:30", null, "2026-10-19T13:30:00Z")]
    [DataRow("Asia/Tokyo", "daily", "09:00", null, "2026-10-20T00:00:00Z")]
    [DataRow("America/New_York", "daily", "09:00", null, "2026-10-19T13:00:00Z")]
    [DataRow("America/New_York", "cron", null, "0 9 * * *", "2026-10-19T13:00:00Z")]
    // New York leaves daylight saving on 1 November, so 09:00 is then 14:00 UTC.
    [DataRow("America/New_York", "cron", null, "0 9 1 11 *", "2026-11-01T14:00:00Z")]
    public async Task InterpretsTimesInTheConfiguredTimeZone(string? timeZone, string kind, string? timeOfDay, string? cron, string expected)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            if (timeZone is not null) builder.UseSetting("Scheduling:TimeZone", timeZone);
        });
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var fields = new Dictionary<string, string> { ["scheduleKind"] = kind };
        if (timeOfDay is not null) fields["timeOfDay"] = timeOfDay;
        if (cron is not null) fields["cron"] = cron;

        var watch = await CreateWatch(client, fields);

        Assert.AreEqual(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            watch["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task RunsADailyWatchAtItsTimeEachDay()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:TimeZone", "Asia/Tokyo"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, new() { ["scheduleKind"] = "daily", ["timeOfDay"] = "09:00" }))["id"]!.GetValue<string>();

        factory.Time.Advance(TimeSpan.FromHours(12));
        await StartAndFinishDueRuns(host);
        var history = await History(client, id);

        Assert.AreEqual(DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            history[0]!["startedAt"]!.GetValue<DateTimeOffset>());
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-21T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    [DataRow("Scheduling:TimeZone", "Mars/Olympus_Mons")]
    [DataRow("Scheduling:MaxConcurrentRuns", "0")]
    public async Task RefusesToStartWithInvalidSchedulingSettings(string setting, string value)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, value));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(setting, error.Message);
    }

    [TestMethod]
    public async Task RejectsAnOnDemandRunWhileAScheduledRunOfTheSameWatchIsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var scheduled = await StartDueRuns(factory);
        await runner.WaitForStarted(1);

        var onDemand = await RunNow(client, id);
        runner.FinishAll();
        await FinishAll(scheduled);

        await AssertConflict(onDemand, "This Watch is already running.");
        await AssertOneRunSetTheBaseline(client, id);
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task SkipsAScheduledRunWhileAnOnDemandRunOfTheSameWatchIsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var onDemand = RunNow(client, id);
        await runner.WaitForStarted(1);

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var scheduled = await StartDueRuns(factory);

        // The scheduler reschedules a Watch from now when it takes its due Run, whether or not it starts it.
        Assert.IsInstanceOfType<Refused>(Assert.ContainsSingle(scheduled));
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        runner.FinishAll();
        (await onDemand).EnsureSuccessStatusCode();
        await AssertOneRunSetTheBaseline(client, id);
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task SkipsAScheduledRunAtOnceWhileAnOnDemandRunOfTheSameWatchWaitsForASlot()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var busy = await OccupyEveryRunSlot(client, runner);
        var onDemand = RunNow(client, id);
        await WaitUntilRunning(factory, id);

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var scheduled = await StartDueRuns(factory);

        Assert.AreEqual(new Refused(RunRefusal.WaitingForSlot, Gated: false), Assert.ContainsSingle(scheduled));
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        runner.FinishAll();
        await runner.WaitForStarted(3);
        runner.FinishAll();
        (await onDemand).EnsureSuccessStatusCode();
        foreach (var response in busy) (await response).EnsureSuccessStatusCode();
        Assert.IsEmpty(await StartDueRuns(factory));
        Assert.HasCount(1, await History(client, id));
    }

    [TestMethod]
    public async Task LeavesItsOwnScheduledRunWaitingForASlotToBeClaimedWhenItStarts()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var busy = await OccupyEveryRunSlot(client, runner);
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var dueAt = (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>();
        var scheduled = await StartDueRuns(factory);

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var again = await StartDueRuns(factory);

        Assert.AreEqual(new Refused(RunRefusal.WaitingForSlot, Gated: true), Assert.ContainsSingle(again));
        Assert.AreEqual(dueAt, (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        runner.FinishAll();
        await runner.WaitForStarted(3);
        runner.FinishAll();
        foreach (var response in busy) (await response).EnsureSuccessStatusCode();
        await FinishAll(scheduled);
        var history = await History(client, id);
        Assert.HasCount(1, history);
        // Claimed once it had a slot, so it's recorded as starting then, and the next Run is scheduled from then.
        Assert.AreEqual(factory.Time.GetUtcNow(), history[0]!["startedAt"]!.GetValue<DateTimeOffset>());
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task DropsAScheduledRunWhoseWatchIsPausedWhileItWaitsForASlot()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var busy = await OccupyEveryRunSlot(client, runner);
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var scheduled = Assert.IsInstanceOfType<Admitted>(Assert.ContainsSingle(await StartDueRuns(factory)));

        await PostWatchAction(client, id, "pause");
        runner.FinishAll();
        var execution = await scheduled.Finished;

        Assert.AreEqual(RunExecutionStatus.NotClaimed, execution.Status);
        Assert.AreEqual(2, runner.Started);
        foreach (var response in busy) (await response).EnsureSuccessStatusCode();
        Assert.IsEmpty(await History(client, id));
        Assert.IsFalse((await Watch(client, id))["isRunning"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SkipsAScheduledRunOfARunningWatchWhileAnEarlierDueWatchWaitsForASlot()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await CreateWatch(client, Every(3, "minutes"));
        var id = (await CreateWatch(client, Every(4, "minutes")))["id"]!.GetValue<string>();
        var other = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        var onDemand = RunNow(client, id);
        await runner.WaitForStarted(1);
        var otherOnDemand = RunNow(client, other);
        await runner.WaitForStarted(2);

        // The 3-minute Watch, due first, waits for a slot; the 4-minute Watch is due too but already running.
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        var scheduled = await StartDueRuns(factory);
        runner.FinishOne();
        (await onDemand).EnsureSuccessStatusCode();
        await runner.WaitForStarted(3);
        runner.FinishAll();
        await FinishAll(scheduled);

        Assert.IsInstanceOfType<Admitted>(scheduled[0]);
        Assert.AreEqual(new Refused(RunRefusal.Executing, Gated: false), scheduled[1]);
        Assert.IsEmpty(await StartDueRuns(factory));
        Assert.AreEqual(3, runner.Started);
        Assert.HasCount(1, await History(client, id));
        (await otherOnDemand).EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task ExplainsThatRunNowIsRejectedWhileTheWatchWaitsForASlot()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(3, "minutes")))["id"]!.GetValue<string>();
        var busy = await OccupyEveryRunSlot(client, runner);
        factory.Time.Advance(TimeSpan.FromMinutes(3));
        var scheduled = await StartDueRuns(factory);

        var runNow = await RunNow(client, id);

        await AssertConflict(runNow, "This Watch is waiting for a free slot to run.");
        runner.FinishAll();
        await runner.WaitForStarted(3);
        runner.FinishAll();
        foreach (var response in busy) (await response).EnsureSuccessStatusCode();
        await FinishAll(scheduled);
    }

    [TestMethod]
    public async Task KeepsAChangedScheduleThreeMinutesAwayFromTheRunThatJustStarted()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await StartAndFinishDueRuns(factory);

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new { scheduleKind = "daily", timeOfDay = "12:06" }, testContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        // The Run started at 12:05, so 12:06 today is too soon.
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T12:08:00Z", System.Globalization.CultureInfo.InvariantCulture),
            (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task KeepsARunRescheduledForANewTimeZoneThreeMinutesAwayFromTheLastRun()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            await using (var before = SharingDatabase(factory, databasePath))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                // 12:05, 12:51, 17:05 and 17:51 in UTC; in Kathmandu (UTC+5:45), 17:51 local is 12:06 UTC.
                id = (await CreateWatch(client, new() { ["scheduleKind"] = "cron", ["cron"] = "5,51 12,17 * * *" }))["id"]!.GetValue<string>();
                factory.Time.Advance(TimeSpan.FromMinutes(5));
                await StartAndFinishDueRuns(before);
            }

            await using var after = SharingDatabase(factory, databasePath, "Asia/Kathmandu");
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T12:08:00Z", System.Globalization.CultureInfo.InvariantCulture),
                (await Watch(restarted, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task ReturnsTheNextRunAsStoredWhenCreatingAWatch()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        // A real clock rarely lands on a whole second.
        factory.Time.Advance(TimeSpan.FromTicks(1_234_567));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var created = await CreateWatch(client, Every(5, "minutes"));

        var listed = await Watch(client, created["id"]!.GetValue<string>());
        Assert.AreEqual(listed["nextRunAt"]!.GetValue<string>(), created["nextRunAt"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DiscardsAScheduledRunEvaluatedAgainstABaselineAnotherInstanceAdvanced()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var executions = 0;
        factory.Runner.OnExecute = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                // Another backend instance, whose Runs this one can't see, records a Run while this one executes.
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Watches.Where(item => item.Id == Guid.Parse(id))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Baseline, "£19")
                        .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
            }
            return Succeeded;
        };

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var discarded = Assert.IsInstanceOfType<Admitted>(Assert.ContainsSingle(await StartDueRuns(factory)));

        Assert.AreEqual(RunExecutionStatus.Superseded, (await discarded.Finished).Status);
        Assert.AreEqual(1, Volatile.Read(ref executions));
        Assert.IsEmpty(await History(client, id));
        Assert.AreEqual("£19", (await Watch(client, id))["baseline"]!.GetValue<string>());

        // The discarded Run still released the Watch, so its next scheduled Run is recorded against the newer Baseline.
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await StartAndFinishDueRuns(factory);
        var history = await History(client, id);
        Assert.HasCount(1, history);
        Assert.AreEqual("£20", history[0]!["content"]!.GetValue<string>());
        Assert.IsTrue(history[0]!["isChange"]!.GetValue<bool>());
    }

    private async Task AssertOneRunSetTheBaseline(HttpClient client, string id)
    {
        var history = await History(client, id);
        Assert.HasCount(1, history);
        Assert.AreEqual("£20", history[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("£20", (await Watch(client, id))["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChangesAWatchsScheduleAndSchedulesItsNextRunFromNow()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(20));

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new { scheduleKind = "daily", timeOfDay = "13:30", jitterMinutes = 0 }, testContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        var listed = await Watch(client, id);
        foreach (var watch in new[] { updated!, listed })
        {
            Assert.AreEqual("daily", watch["schedule"]!["kind"]!.GetValue<string>());
            Assert.AreEqual("13:30:00", watch["schedule"]!["timeOfDay"]!.GetValue<string>());
            Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T13:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
                watch["nextRunAt"]!.GetValue<DateTimeOffset>());
        }

        await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new { scheduleKind = "cron", cron = "*/10 * * * *", jitterMinutes = 4 }, testContext.CancellationToken);
        listed = await Watch(client, id);
        Assert.AreEqual("*/10 * * * *", listed["schedule"]!["cron"]!.GetValue<string>());
        Assert.IsNull(listed["schedule"]!["timeOfDay"]);
        Assert.AreEqual(4, listed["jitterMinutes"]!.GetValue<int>());
        var nextRunAt = listed["nextRunAt"]!.GetValue<DateTimeOffset>();
        Assert.IsTrue(nextRunAt >= factory.Time.GetUtcNow().AddMinutes(10) && nextRunAt <= factory.Time.GetUtcNow().AddMinutes(14),
            nextRunAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task KeepsTheScheduleWhenAChangeIsRejected()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, Every(1, "hours"));
        var id = watch["id"]!.GetValue<string>();

        var tooOften = await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new { scheduleKind = "cron", cron = "*/2 * * * *" }, testContext.CancellationToken);
        var tooMuchJitter = await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new { scheduleKind = "interval", every = 2, unit = "hours", jitterMinutes = 90 }, testContext.CancellationToken);
        var missing = await client.PutAsJsonAsync($"/api/watches/{Guid.NewGuid()}/schedule",
            new { scheduleKind = "interval", every = 2, unit = "hours" }, testContext.CancellationToken);

        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, tooOften.StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, tooMuchJitter.StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        var listed = await Watch(client, id);
        Assert.AreEqual(watch["schedule"]!.ToJsonString(), listed["schedule"]!.ToJsonString());
        Assert.AreEqual(watch["nextRunAt"]!.GetValue<DateTimeOffset>(), listed["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    [DataRow("daily", "09:00", null)]
    [DataRow("cron", null, "0 9 * * *")]
    public async Task ReschedulesWatchesForANewTimeZoneWhenLookoutRestarts(string kind, string? timeOfDay, string? cron)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            var fields = new Dictionary<string, string> { ["scheduleKind"] = kind };
            if (timeOfDay is not null) fields["timeOfDay"] = timeOfDay;
            if (cron is not null) fields["cron"] = cron;
            string id;
            await using (var before = SharingDatabase(factory, databasePath))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                var watch = await CreateWatch(client, fields);
                id = watch["id"]!.GetValue<string>();
                Assert.AreEqual(DateTimeOffset.Parse("2026-10-20T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    watch["nextRunAt"]!.GetValue<DateTimeOffset>());
            }

            await using var after = SharingDatabase(factory, databasePath, "Asia/Tokyo");
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.AreEqual(DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                (await Watch(restarted, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
            factory.Time.Advance(TimeSpan.FromHours(12));
            await StartAndFinishDueRuns(after);
            var history = await History(restarted, id);
            Assert.AreEqual(DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                history[0]!["startedAt"]!.GetValue<DateTimeOffset>());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task KeepsPendingRunsWhenLookoutRestartsInTheSameTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            var schedule = new Dictionary<string, string> { ["scheduleKind"] = "cron", ["cron"] = "0 9 * * *", ["jitterMinutes"] = "60" };
            var created = new List<JsonObject>();
            await using (var before = SharingDatabase(factory, databasePath, "Asia/Tokyo"))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                for (var index = 0; index < 5; index++)
                {
                    created.Add(await CreateWatch(client, schedule));
                }
            }

            await using var after = SharingDatabase(factory, databasePath, "Asia/Tokyo");
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            foreach (var watch in created)
            {
                Assert.AreEqual(watch["nextRunAt"]!.GetValue<DateTimeOffset>(),
                    (await Watch(restarted, watch["id"]!.GetValue<string>()))["nextRunAt"]!.GetValue<DateTimeOffset>());
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    [DataRow("interval", "2", "minutes", null)]
    [DataRow("interval", "0", "hours", null)]
    [DataRow("interval", "-1", "days", null)]
    [DataRow("cron", null, null, "* * * * *")]
    [DataRow("cron", null, null, "*/2 * * * *")]
    // Only the gap between two occurrences each day is too short.
    [DataRow("cron", null, null, "0,1 9 * * *")]
    [DataRow("cron", null, null, "59,0 23,0 * * *")]
    public async Task RejectsSchedulesThatRunMoreThanOnceEveryThreeMinutes(string kind, string? every, string? unit, string? cron)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await PostWatch(client, Fields(kind, every, unit, cron));

        await AssertRejected(client, response, "Schedules must not run more than once every 3 minutes.");
    }

    [TestMethod]
    [DataRow("interval", "3", "minutes", null)]
    [DataRow("interval", "365", "days", null)]
    [DataRow("cron", null, null, "*/3 * * * *")]
    [DataRow("cron", null, null, "0,3 9 * * *")]
    [DataRow("cron", null, null, "0 9 * * 1-5")]
    public async Task AcceptsSchedulesThatRunAtMostOnceEveryThreeMinutes(string kind, string? every, string? unit, string? cron)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await PostWatch(client, Fields(kind, every, unit, cron));

        Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    [TestMethod]
    [DataRow("cron", null, null, "not cron", "Enter a cron expression with five fields")]
    [DataRow("cron", null, null, "0 0 9 * * *", "Enter a cron expression with five fields")]
    [DataRow("cron", null, null, "", "Enter a cron expression with five fields")]
    [DataRow("cron", null, null, "0 0 30 2 *", "This cron expression never runs.")]
    [DataRow("interval", null, "minutes", null, "An interval schedule needs a number")]
    [DataRow("interval", "5", null, null, "An interval schedule needs a number")]
    [DataRow("interval", "366", "days", null, "at least once every 365 days")]
    [DataRow("interval", "2147483647", "days", null, "at least once every 365 days")]
    [DataRow("daily", null, null, null, "A daily schedule needs a time of day.")]
    [DataRow(null, null, null, null, "Choose an interval, daily or cron schedule.")]
    public async Task RejectsIncompleteOrInvalidSchedules(string? kind, string? every, string? unit, string? cron, string message)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await PostWatch(client, Fields(kind, every, unit, cron));

        await AssertRejected(client, response, message);
    }

    [TestMethod]
    public async Task DelaysEachRunByUpToItsJitter()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = factory.Time.GetUtcNow();
        var schedule = Every(10, "minutes");
        schedule["jitterMinutes"] = "5";

        var watches = new List<JsonObject>();
        for (var index = 0; index < 20; index++)
        {
            watches.Add(await CreateWatch(client, schedule));
        }

        var nextRuns = watches.Select(watch => watch["nextRunAt"]!.GetValue<DateTimeOffset>()).ToList();
        Assert.IsTrue(nextRuns.All(next => next >= created.AddMinutes(10) && next <= created.AddMinutes(15)),
            string.Join(", ", nextRuns));
        Assert.IsGreaterThan(1, nextRuns.Distinct().Count(), "Jitter should vary the next Run.");

        factory.Time.Advance(TimeSpan.FromMinutes(15));
        await StartAndFinishDueRuns(factory);
        foreach (var watch in watches)
        {
            Assert.HasCount(1, await History(client, watch["id"]!.GetValue<string>()));
        }
        var rescheduled = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        var now = factory.Time.GetUtcNow();
        Assert.IsTrue(rescheduled!.Select(watch => watch!["nextRunAt"]!.GetValue<DateTimeOffset>())
            .All(next => next >= now.AddMinutes(10) && next <= now.AddMinutes(15)));
    }

    [TestMethod]
    public async Task NeverSchedulesARunWithinThreeMinutesOfTheLastOneDespiteJitter()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var schedule = new Dictionary<string, string> { ["scheduleKind"] = "cron", ["cron"] = "*/3 * * * *", ["jitterMinutes"] = "2" };
        var ids = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            ids.Add((await CreateWatch(client, schedule))["id"]!.GetValue<string>());
        }

        // Every Watch's first Run is due by 12:05; a Run delayed past the next occurrence must not bunch up with it.
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await StartAndFinishDueRuns(factory);
        foreach (var id in ids)
        {
            var startedAt = (await History(client, id))[0]!["startedAt"]!.GetValue<DateTimeOffset>();
            var nextRunAt = (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>();
            Assert.IsGreaterThanOrEqualTo(startedAt.AddMinutes(3), nextRunAt);
        }
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("61")]
    public async Task RejectsJitterOutsideZeroToSixtyMinutes(string jitter)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var schedule = Every(10, "minutes");
        schedule["jitterMinutes"] = jitter;

        var response = await PostWatch(client, schedule);

        await AssertRejected(client, response, "Jitter must be between 0 and 60 minutes.");
    }

    private static Dictionary<string, string> Fields(string? kind, string? every, string? unit, string? cron)
    {
        var fields = new Dictionary<string, string>();
        if (kind is not null) fields["scheduleKind"] = kind;
        if (every is not null) fields["every"] = every;
        if (unit is not null) fields["unit"] = unit;
        if (cron is not null) fields["cron"] = cron;
        return fields;
    }

    private async Task AssertRejected(HttpClient client, HttpResponseMessage response, string message)
    {
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.Contains(message, problem!["detail"]!.GetValue<string>());
        Assert.IsEmpty((await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!);
    }

    /// <summary>A host that keeps its database when stopped, so another host can start against it like a restart.</summary>
    private static WebApplicationFactory<Program> SharingDatabase(LookoutWebApplicationFactory factory, string databasePath,
        string? timeZone = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False");
            if (timeZone is not null) builder.UseSetting("Scheduling:TimeZone", timeZone);
        });

    private static Dictionary<string, string> Every(int every, string unit) =>
        new() { ["scheduleKind"] = "interval", ["every"] = every.ToString(System.Globalization.CultureInfo.InvariantCulture), ["unit"] = unit };

    private async Task<JsonObject> CreateWatch(HttpClient client, Dictionary<string, string> schedule)
    {
        var response = await PostWatch(client, schedule);
        Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(testContext.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> PostWatch(HttpClient client, Dictionary<string, string> schedule)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default {}"), "script", "price.ts" }
        };
        foreach (var (name, value) in schedule)
        {
            form.Add(new StringContent(value), name);
        }
        return await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
    }

    /// <summary>Runs an action once, just before the first command containing the fragment, as another request would.</summary>
    private sealed class BeforeCommand(string fragment, Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(fragment, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await action();
            }
            return result;
        }
    }

    private async Task PostWatchAction(HttpClient client, string id, string action)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/{action}", UriKind.Relative), null, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonObject> Watch(HttpClient client, string id)
    {
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        return watches!.Single(watch => watch!["id"]!.GetValue<string>() == id)!.AsObject();
    }

    private async Task<JsonArray> History(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken))!;

    private async Task<JsonArray> WaitForHistory(HttpClient client, string id, int count)
    {
        var history = await History(client, id);
        for (var attempt = 0; attempt < 100 && history.Count < count; attempt++)
        {
            await Task.Delay(50, testContext.CancellationToken);
            history = await History(client, id);
        }
        Assert.HasCount(count, history);
        return history;
    }

    /// <summary>Starts the Runs of the Watches due now, as the scheduler's timer would.</summary>
    private Task<IReadOnlyList<Admission>> StartDueRuns(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<Scheduler>().StartDueRunsAsync(testContext.CancellationToken);

    private async Task StartAndFinishDueRuns(WebApplicationFactory<Program> host) => await FinishAll(await StartDueRuns(host));

    /// <summary>Waits for every admitted Run to be recorded or discarded.</summary>
    private static async Task FinishAll(IEnumerable<Admission> admissions) =>
        await Task.WhenAll(admissions.OfType<Admitted>().Select(admitted => admitted.Finished));

    /// <summary>Runs two other Watches now and holds them, which takes both of the default Run slots.</summary>
    private async Task<List<Task<HttpResponseMessage>>> OccupyEveryRunSlot(HttpClient client, BlockingRunner runner)
    {
        var busy = new List<Task<HttpResponseMessage>>();
        for (var index = 0; index < 2; index++)
        {
            var other = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
            busy.Add(RunNow(client, other));
            await runner.WaitForStarted(runner.Started + 1);
        }
        return busy;
    }

    /// <summary>Waits until a Run of the Watch is admitted, such as one a request still on its way asked for.</summary>
    private async Task WaitUntilRunning(WebApplicationFactory<Program> host, string id)
    {
        var runs = host.Services.GetRequiredService<Runs>();
        for (var attempt = 0; attempt < 100 && !runs.IsRunning(Guid.Parse(id)); attempt++)
        {
            await Task.Delay(50, testContext.CancellationToken);
        }
        Assert.IsTrue(runs.IsRunning(Guid.Parse(id)));
    }

    private Task<HttpResponseMessage> RunNow(HttpClient client, string id) =>
        client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);

    private async Task AssertConflict(HttpResponseMessage response, string detail)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual(detail, problem!["detail"]!.GetValue<string>());
    }
}
