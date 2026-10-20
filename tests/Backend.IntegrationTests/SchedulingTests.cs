using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class SchedulingTests(TestContext testContext)
{
    private static readonly RunnerResult Succeeded = new() { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };

    [TestMethod]
    public async Task RunsAWatchWhenItIsDue()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = factory.Time.GetUtcNow();
        var watch = await CreateWatch(client, Every(5, "minutes"));
        var id = watch["id"]!.GetValue<string>();
        Assert.AreEqual(created.AddMinutes(5), watch["nextRunAt"]!.GetValue<DateTimeOffset>());

        factory.Time.Advance(TimeSpan.FromMinutes(4));
        await Settle();
        Assert.IsEmpty(await History(client, id));

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var history = await WaitForHistory(client, id, 1);

        Assert.AreEqual("succeeded", history[0]!["outcome"]!.GetValue<string>());
        Assert.AreEqual(created.AddMinutes(5), history[0]!["startedAt"]!.GetValue<DateTimeOffset>());
        var listed = await Watch(client, id);
        Assert.AreEqual("£20", listed["baseline"]!.GetValue<string>());
        Assert.AreEqual(created.AddMinutes(10), listed["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task RunsOnceAfterTimePassesSeveralMissedRunsThenSchedulesFromNow()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, Every(5, "minutes"));
        var id = watch["id"]!.GetValue<string>();

        factory.Time.Advance(TimeSpan.FromHours(1));
        await WaitForHistory(client, id, 1);
        await Settle();

        Assert.HasCount(1, await History(client, id));
        Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task SkipsRunsMissedWhileLookoutWasDown()
    {
        await using var factory = new LookoutWebApplicationFactory();
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
            await Settle();

            Assert.IsEmpty(await History(restarted, id));
            Assert.AreEqual(factory.Time.GetUtcNow().AddMinutes(5), (await Watch(restarted, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
            factory.Time.Advance(TimeSpan.FromMinutes(5));
            await WaitForHistory(restarted, id, 1);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task RunsAtMostTwoWatchesAtOnceAndStartsTheRestAsRunsFinish()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        for (var index = 0; index < 3; index++)
        {
            await CreateWatch(client, Every(5, "minutes"));
        }

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await runner.WaitForStarted(2);
        await Settle();
        Assert.AreEqual(2, runner.Started);

        runner.FinishOne();
        await runner.WaitForStarted(3);
        runner.FinishAll();
    }

    [TestMethod]
    public async Task OnDemandRunsShareTheLimitOnRunsAtOnce()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await CreateWatch(client, Every(5, "minutes"));
        await CreateWatch(client, Every(5, "minutes"));
        var onDemand = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await runner.WaitForStarted(2);

        var runNow = client.PostAsync(new Uri($"/api/watches/{onDemand}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await Settle();
        Assert.AreEqual(2, runner.Started);
        Assert.IsFalse(runNow.IsCompleted);

        runner.FinishOne();
        await runner.WaitForStarted(3);
        runner.FinishAll();
        (await runNow).EnsureSuccessStatusCode();
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
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:TimeZone", "Asia/Tokyo"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, new() { ["scheduleKind"] = "daily", ["timeOfDay"] = "09:00" }))["id"]!.GetValue<string>();

        factory.Time.Advance(TimeSpan.FromHours(12));
        var history = await WaitForHistory(client, id, 1);

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
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(setting, value));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(setting, error.Message);
    }

    [TestMethod]
    public async Task RejectsAnOnDemandRunWhileAScheduledRunOfTheSameWatchIsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await runner.WaitForStarted(1);

        var onDemand = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        runner.FinishAll();

        Assert.AreEqual(System.Net.HttpStatusCode.Conflict, onDemand.StatusCode);
        await AssertOneRunSetTheBaseline(client, id);
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task SkipsAScheduledRunWhileAnOnDemandRunOfTheSameWatchIsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(5, "minutes")))["id"]!.GetValue<string>();
        var onDemand = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await runner.WaitForStarted(1);

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        // The scheduler reschedules a Watch from now when it takes its due Run, whether or not it starts it.
        var rescheduledTo = factory.Time.GetUtcNow().AddMinutes(5);
        for (var attempt = 0; attempt < 100 && (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>() != rescheduledTo; attempt++)
        {
            await Task.Delay(50, testContext.CancellationToken);
        }
        Assert.AreEqual(rescheduledTo, (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>());
        runner.FinishAll();

        (await onDemand).EnsureSuccessStatusCode();
        await AssertOneRunSetTheBaseline(client, id);
        Assert.AreEqual(1, runner.Started);
    }

    [TestMethod]
    public async Task SkipsAScheduledRunOfARunningWatchEvenWhileEverySlotIsBusy()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Every(3, "minutes")))["id"]!.GetValue<string>();
        var other = (await CreateWatch(client, Every(1, "hours")))["id"]!.GetValue<string>();
        var onDemand = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await runner.WaitForStarted(1);
        var otherOnDemand = client.PostAsync(new Uri($"/api/watches/{other}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await runner.WaitForStarted(2);

        factory.Time.Advance(TimeSpan.FromMinutes(3));
        await Settle();
        // The Watch's own on-demand Run finishes first, freeing a slot while its due Run is still being decided.
        runner.FinishOne();
        (await onDemand).EnsureSuccessStatusCode();
        await Settle();

        Assert.AreEqual(2, runner.Started);
        Assert.HasCount(1, await History(client, id));
        runner.FinishAll();
        (await otherOnDemand).EnsureSuccessStatusCode();
    }

    private async Task AssertOneRunSetTheBaseline(HttpClient client, string id)
    {
        var history = await WaitForHistory(client, id, 1);
        await Settle();
        Assert.HasCount(1, await History(client, id));
        Assert.AreEqual("£20", history[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("£20", (await Watch(client, id))["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChangesAWatchsScheduleAndSchedulesItsNextRunFromNow()
    {
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
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
            var history = await WaitForHistory(restarted, id, 1);
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
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
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
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await PostWatch(client, Fields(kind, every, unit, cron));

        await AssertRejected(client, response, message);
    }

    [TestMethod]
    public async Task DelaysEachRunByUpToItsJitter()
    {
        await using var factory = new LookoutWebApplicationFactory();
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
        foreach (var watch in watches)
        {
            await WaitForHistory(client, watch["id"]!.GetValue<string>(), 1);
        }
        var rescheduled = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        var now = factory.Time.GetUtcNow();
        Assert.IsTrue(rescheduled!.Select(watch => watch!["nextRunAt"]!.GetValue<DateTimeOffset>())
            .All(next => next >= now.AddMinutes(10) && next <= now.AddMinutes(15)));
    }

    [TestMethod]
    public async Task NeverSchedulesARunWithinThreeMinutesOfTheLastOneDespiteJitter()
    {
        await using var factory = new LookoutWebApplicationFactory();
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
        foreach (var id in ids)
        {
            var startedAt = (await WaitForHistory(client, id, 1))[0]!["startedAt"]!.GetValue<DateTimeOffset>();
            var nextRunAt = (await Watch(client, id))["nextRunAt"]!.GetValue<DateTimeOffset>();
            Assert.IsGreaterThanOrEqualTo(startedAt.AddMinutes(3), nextRunAt);
        }
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("61")]
    public async Task RejectsJitterOutsideZeroToSixtyMinutes(string jitter)
    {
        await using var factory = new LookoutWebApplicationFactory();
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

    /// <summary>Holds each Run open until the test finishes it.</summary>
    private sealed class BlockingRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource<RunnerResult>> _pending = new();
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public Task<RunnerResult> Execute(RunnerRequest request, CancellationToken cancellationToken)
        {
            var run = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(run);
            Interlocked.Increment(ref _started);
            return run.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForStarted(int count)
        {
            for (var attempt = 0; attempt < 100 && Started < count; attempt++)
            {
                await Task.Delay(50);
            }
            Assert.AreEqual(count, Started);
        }

        public void FinishOne()
        {
            Assert.IsTrue(_pending.TryDequeue(out var run));
            run.SetResult(Succeeded);
        }

        public void FinishAll()
        {
            while (_pending.TryDequeue(out var run))
            {
                run.SetResult(Succeeded);
            }
        }
    }

    /// <summary>Gives the scheduler real time to act on time a test advanced, before asserting nothing happened.</summary>
    private Task Settle() => Task.Delay(300, testContext.CancellationToken);
}
