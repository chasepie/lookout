using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using static ChasePie.Lookout.Backend.IntegrationTests.SchedulingTests;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The install-wide time zone that daily and cron Schedules use, which the user changes in Settings.
/// It is 12:00 UTC on Monday 19 October 2026: 21:00 in Tokyo (UTC+9) and 08:00 in New York (UTC-4, daylight saving).
/// </summary>
[TestClass]
public sealed class TimeZoneTests(TestContext testContext)
{
    [TestMethod]
    public async Task ShowsTheConfiguredTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:TimeZone", "Asia/Tokyo"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);

        Assert.AreEqual("Asia/Tokyo", await TimeZone(client));
    }

    [TestMethod]
    public async Task StartsWithTheHostsTimeZoneWhenNoneIsConfigured()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        Assert.AreEqual("Pacific/Auckland", await TimeZone(client));
    }

    [TestMethod]
    public async Task SavesAConfiguredWindowsTimeZoneAsItsIanaTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:TimeZone", "Eastern Standard Time"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);

        Assert.AreEqual("America/New_York", await TimeZone(client));
        Assert.AreEqual(HttpStatusCode.OK, (await ChangeTimeZone(client, await TimeZone(client))).StatusCode);
    }

    [TestMethod]
    public async Task SavesTheHostsWindowsTimeZoneAsItsIanaTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        Assert.AreEqual("Asia/Tokyo", await TimeZone(client));
        Assert.AreEqual(HttpStatusCode.OK, (await ChangeTimeZone(client, await TimeZone(client))).StatusCode);
    }

    [TestMethod]
    public async Task StartsInUtcWhenTheHostsTimeZoneHasNoIanaTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Lookout Test Time", TimeSpan.FromHours(3), "Test", "Test"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        Assert.AreEqual("UTC", await TimeZone(client));
    }

    [TestMethod]
    public async Task ShowsTheTimeZoneSchedulesFallBackToWhenTheHostDoesNotKnowTheSavedOne()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Scheduling:TimeZone", "Asia/Tokyo"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        // As if the host's time zone data no longer has the one saved.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Settings.ExecuteUpdateAsync(
                setters => setters.SetProperty(settings => settings.TimeZone, "Mars/Olympus_Mons"), testContext.CancellationToken);
        }

        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();

        Assert.AreEqual("Mars/Olympus_Mons", settings!["timeZone"]!.GetValue<string>());
        Assert.AreEqual("Asia/Tokyo", settings["effectiveTimeZone"]!.GetValue<string>());
        Assert.IsFalse(settings["timeZoneKnown"]!.GetValue<bool>());
        Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    public async Task SchedulesInTheSavedTimeZoneWhenTheHostKnowsIt()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await ChangeTimeZone(client, "Europe/London")).EnsureSuccessStatusCode();

        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);

        Assert.AreEqual("Europe/London", settings!["effectiveTimeZone"]!.GetValue<string>());
        Assert.IsTrue(settings["timeZoneKnown"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task KeepsASavedTimeZoneThroughRestartsWhateverIsConfigured()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var databasePath = NewDatabasePath();
        try
        {
            await using (var before = SharingDatabase(factory, databasePath, "Asia/Tokyo"))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                var response = await ChangeTimeZone(client, "Europe/London");

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var saved = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
                Assert.AreEqual("Europe/London", saved!["timeZone"]!.GetValue<string>());
                Assert.AreEqual("Europe/London", await TimeZone(client));
            }

            await using var after = SharingDatabase(factory, databasePath, "America/New_York");
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.AreEqual("Europe/London", await TimeZone(restarted));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task KeepsTheTimeZoneAnInstallStartedWithWhenItsConfigurationChanges()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var databasePath = NewDatabasePath();
        try
        {
            string id;
            await using (var before = SharingDatabase(factory, databasePath, "Asia/Tokyo"))
            {
                using var client = await before.CreateSignedInClientAsync(testContext.CancellationToken);
                id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();
            }

            await using var after = SharingDatabase(factory, databasePath, "America/New_York");
            using var restarted = await after.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.AreEqual("Asia/Tokyo", await TimeZone(restarted));
            Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(restarted, id));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task AnExistingInstallStartsWithItsConfiguredTimeZoneAndKeepsItsOtherSettings()
    {
        var databasePath = NewDatabasePath();
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using (var database = new LookoutDbContext(new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite(connectionString).Options))
            {
                // The last migration before Lookout saved its time zone.
                await database.GetService<IMigrator>().MigrateAsync("NotificationChannels", testContext.CancellationToken);
                await database.Settings.ExecuteUpdateAsync(setters => setters.SetProperty(settings => settings.RetentionDays, 7),
                    testContext.CancellationToken);
            }
            await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
            using var client = await SharingDatabase(factory, databasePath, "Asia/Tokyo").CreateSignedInClientAsync(testContext.CancellationToken);

            var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);

            Assert.AreEqual("Asia/Tokyo", settings!["timeZone"]!.GetValue<string>());
            Assert.AreEqual(7, settings["retentionDays"]!.GetValue<int>());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    [DataRow("daily", "09:00")]
    [DataRow("cron", "0 9 * * *")]
    public async Task RefusesADailyOrCronWatchGivenInATimeZoneThatIsNoLongerTheInstalls(string kind, string time)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        // As if another tab changed it while this one's form still named UTC.
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        var fields = kind == "daily" ? Daily(time) : Cron(time);
        fields["timeZone"] = "UTC";

        var response = await PostWatch(client, fields);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.Contains("changed to Asia/Tokyo", problem!["detail"]!.GetValue<string>());
        Assert.IsEmpty((await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!);
    }

    [TestMethod]
    public async Task CreatesADailyWatchGivenInTheInstallsTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        var fields = Daily("09:00");
        fields["timeZone"] = "Asia/Tokyo";

        var response = await PostWatch(client, fields);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
        Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    public async Task CreatesAnIntervalWatchWhateverTimeZoneItWasGivenIn()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();

        var response = await PostWatch(client,
            new() { ["scheduleKind"] = "interval", ["every"] = "1", ["unit"] = "hours", ["timeZone"] = "UTC" });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    [TestMethod]
    [DataRow("daily", "10:00")]
    [DataRow("cron", "0 10 * * *")]
    public async Task RefusesAScheduleEditGivenInATimeZoneThatIsNoLongerTheInstalls(string kind, string time)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, Daily("09:00"));
        var id = watch["id"]!.GetValue<string>();
        // As if another tab changed it while this one's form still named UTC.
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        var before = await Watch(client, id);
        var fields = kind == "daily" ? Daily(time) : Cron(time);
        fields["timeZone"] = "UTC";

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule", fields, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.Contains("changed to Asia/Tokyo", problem!["detail"]!.GetValue<string>());
        var after = await Watch(client, id);
        Assert.AreEqual(before["schedule"]!.ToJsonString(), after["schedule"]!.ToJsonString());
        Assert.AreEqual(before["nextRunAt"]!.GetValue<DateTimeOffset>(), after["nextRunAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    public async Task SavesAScheduleEditGivenInTheInstallsTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        var fields = Daily("10:00");
        fields["timeZone"] = "Asia/Tokyo";

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule", fields, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(At("2026-10-20T01:00:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    public async Task SavesAnIntervalScheduleEditWhateverTimeZoneItWasGivenIn()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule",
            new Dictionary<string, string> { ["scheduleKind"] = "interval", ["every"] = "1", ["unit"] = "hours", ["timeZone"] = "UTC" },
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task PushesAChangeOfTimeZoneToTheApp()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);

        // Neither saving the time zone already saved nor an unknown one changes it, so only the last save is pushed.
        (await ChangeTimeZone(client, "UTC")).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await ChangeTimeZone(client, "Mars/Olympus_Mons")).StatusCode);
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();

        var pushed = Assert.ContainsSingle(await listener.NextAsync(1, testContext.CancellationToken));
        Assert.AreEqual(nameof(IWatchEventsClient.TimeZoneChanged), pushed.Name);
        Assert.AreEqual("Asia/Tokyo", pushed.Payload.GetProperty("timeZone").GetString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    [DataRow("Mars/Olympus_Mons")]
    [DataRow("Eastern Standard Time")]
    [DataRow("../../etc/passwd")]
    public async Task RejectsAnEmptyOrUnknownTimeZoneAndKeepsTheSavedOne(string? timeZone)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, Daily("09:00", jitterMinutes: 30));

        var response = await ChangeTimeZone(client, timeZone);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.Contains("such as Europe/London", problem!["detail"]!.GetValue<string>());
        Assert.AreEqual("UTC", await TimeZone(client));
        Assert.AreEqual(watch["nextRunAt"]!.GetValue<DateTimeOffset>(), await NextRunAt(client, watch["id"]!.GetValue<string>()));
    }

    [TestMethod]
    [DataRow("daily", "09:00", "Asia/Tokyo", "2026-10-20T00:00:00Z")]
    [DataRow("cron", "0 9 * * *", "America/New_York", "2026-10-19T13:00:00Z")]
    // New York leaves daylight saving on 1 November, so 09:00 is then 14:00 UTC.
    [DataRow("cron", "0 9 1 11 *", "America/New_York", "2026-11-01T14:00:00Z")]
    // Kathmandu is UTC+5:45 all year.
    [DataRow("daily", "18:00", "Asia/Kathmandu", "2026-10-19T12:15:00Z")]
    public async Task SchedulesDailyAndCronWatchesInTheNewTimeZoneWithoutARestart(string kind, string time, string timeZone,
        string expected)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, kind == "daily" ? Daily(time) : Cron(time)))["id"]!.GetValue<string>();

        (await ChangeTimeZone(client, timeZone)).EnsureSuccessStatusCode();

        Assert.AreEqual(timeZone, await TimeZone(client));
        Assert.AreEqual(At(expected), await NextRunAt(client, id));
        factory.Time.Advance(At(expected) - factory.Time.GetUtcNow());
        await StartAndFinishDueRuns(factory);
        Assert.AreEqual(At(expected), Assert.ContainsSingle(await History(client, id))!["startedAt"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    [DataRow("daily", "01:30")]
    [DataRow("cron", "30 1 * * *")]
    public async Task RunsOnceAtTheEndOfAGapWhenTheClocksGoForwardOverItsTime(string kind, string time)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        // London goes from 01:00 GMT to 02:00 BST on Sunday 28 March 2027, so that night has no 01:30.
        factory.Time.Advance(At("2027-03-27T12:00:00Z") - factory.Time.GetUtcNow());
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await ChangeTimeZone(client, "Europe/London")).EnsureSuccessStatusCode();
        var id = (await CreateWatch(client, kind == "daily" ? Daily(time) : Cron(time)))["id"]!.GetValue<string>();

        // 02:00 BST, when the gap ends.
        Assert.AreEqual(At("2027-03-28T01:00:00Z"), await NextRunAt(client, id));
        factory.Time.Advance(At("2027-03-28T01:00:00Z") - factory.Time.GetUtcNow());
        await StartAndFinishDueRuns(factory);

        Assert.HasCount(1, await History(client, id));
        // 01:30 BST the next day.
        Assert.AreEqual(At("2027-03-29T00:30:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    [DataRow("daily", "01:30")]
    [DataRow("cron", "30 1 * * *")]
    public async Task RunsOnceWhenTheClocksGoBackOverItsTime(string kind, string time)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        // London goes from 02:00 BST back to 01:00 GMT on Sunday 25 October 2026, so that night has 01:30 twice.
        factory.Time.Advance(At("2026-10-24T12:00:00Z") - factory.Time.GetUtcNow());
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await ChangeTimeZone(client, "Europe/London")).EnsureSuccessStatusCode();
        var id = (await CreateWatch(client, kind == "daily" ? Daily(time) : Cron(time)))["id"]!.GetValue<string>();

        // The first 01:30, in BST.
        Assert.AreEqual(At("2026-10-25T00:30:00Z"), await NextRunAt(client, id));
        factory.Time.Advance(At("2026-10-25T00:30:00Z") - factory.Time.GetUtcNow());
        await StartAndFinishDueRuns(factory);

        Assert.HasCount(1, await History(client, id));
        // Not the second 01:30, in GMT, an hour later, but 01:30 GMT the next day.
        Assert.AreEqual(At("2026-10-26T01:30:00Z"), await NextRunAt(client, id));
        factory.Time.Advance(TimeSpan.FromHours(1));
        Assert.IsEmpty(await StartDueRuns(factory));
    }

    [TestMethod]
    public async Task AWatchPausedWhileTheTimeZoneChangesStaysPausedAndResumesInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        HttpClient? client = null;
        // The pause's update, which pauses the Watch only if it isn't already.
        var changeTimeZone = new BeforeCommand("NOT (\"w\".\"IsPaused\")",
            async () => (await ChangeTimeZone(client!, "Asia/Tokyo")).EnsureSuccessStatusCode());
        await using var host = WithInterceptor(factory, changeTimeZone);
        client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();

        await PostWatchAction(client, id, "pause");

        Assert.IsTrue(changeTimeZone.Fired);
        Assert.IsTrue((await Watch(client, id))["isPaused"]!.GetValue<bool>());
        Assert.IsNull(await NextRunAt(client, id));
        await PostWatchAction(client, id, "resume");
        // 09:00 in Tokyo is 00:00 UTC.
        Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(client, id));
        client.Dispose();
    }

    [TestMethod]
    public async Task KeepsEachWatchsJitterInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add((await CreateWatch(client, Daily("09:00", jitterMinutes: 30)))["id"]!.GetValue<string>());
        }

        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();

        foreach (var id in ids)
        {
            var nextRunAt = await NextRunAt(client, id);
            Assert.IsTrue(nextRunAt >= At("2026-10-20T00:00:00Z") && nextRunAt <= At("2026-10-20T00:30:00Z"),
                nextRunAt?.ToString(CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public async Task DoesNotCatchUpOnARunTheNewTimeZonePutsInThePast()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("12:30")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(10));

        // 12:30 in London (UTC+1 until 25 October) was 11:30 UTC, an hour ago.
        (await ChangeTimeZone(client, "Europe/London")).EnsureSuccessStatusCode();

        Assert.IsEmpty(await StartDueRuns(factory));
        Assert.AreEqual(At("2026-10-20T11:30:00Z"), await NextRunAt(client, id));
        Assert.IsEmpty(await History(client, id));
    }

    [TestMethod]
    public async Task KeepsARunRescheduledForANewTimeZoneThreeMinutesAwayFromTheLastRun()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        // 12:05, 12:51, 17:05 and 17:51 in UTC; in Kathmandu (UTC+5:45), 17:51 local is 12:06 UTC.
        var id = (await CreateWatch(client, Cron("5,51 12,17 * * *")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await StartAndFinishDueRuns(factory);

        (await ChangeTimeZone(client, "Asia/Kathmandu")).EnsureSuccessStatusCode();

        Assert.AreEqual(At("2026-10-19T12:08:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    public async Task KeepsIntervalWatchesNextRunAndPausedWatchesUnscheduledUntilResumedInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var interval = await CreateWatch(client, new() { ["scheduleKind"] = "interval", ["every"] = "5", ["unit"] = "hours", ["jitterMinutes"] = "60" });
        var pausedId = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();
        await PostWatchAction(client, pausedId, "pause");
        factory.Time.Advance(TimeSpan.FromMinutes(20));

        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();

        Assert.AreEqual(interval["nextRunAt"]!.GetValue<DateTimeOffset>(), await NextRunAt(client, interval["id"]!.GetValue<string>()));
        Assert.IsNull(await NextRunAt(client, pausedId));
        await PostWatchAction(client, pausedId, "resume");
        Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(client, pausedId));
    }

    [TestMethod]
    public async Task ARunInProgressKeepsItsResultWhenTheTimeZoneChanges()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        var runner = new BlockingRunner();
        factory.Runner.OnExecute = runner.Execute;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("12:05")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var started = await StartDueRuns(factory);
        await runner.WaitForStarted(1);

        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        runner.FinishAll();
        await Task.WhenAll(started.OfType<Admitted>().Select(admitted => admitted.Finished));

        var history = Assert.ContainsSingle(await History(client, id));
        Assert.AreEqual("succeeded", history!["outcome"]!.GetValue<string>());
        Assert.AreEqual("£20", (await Watch(client, id))["baseline"]!.GetValue<string>());
        // 12:05 in Tokyo is 03:05 UTC.
        Assert.AreEqual(At("2026-10-20T03:05:00Z"), await NextRunAt(client, id));
    }

    [TestMethod]
    public async Task ChangingTheTimeZoneAndTheRetentionPeriodKeepEachOther()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        (await client.PutAsJsonAsync("/api/settings", new { retentionDays = 7 }, testContext.CancellationToken)).EnsureSuccessStatusCode();
        (await ChangeTimeZone(client, "Asia/Tokyo")).EnsureSuccessStatusCode();
        var retention = await client.PutAsJsonAsync("/api/settings", new { retentionDays = 9 }, testContext.CancellationToken);

        var saved = await retention.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("Asia/Tokyo", saved!["timeZone"]!.GetValue<string>());
        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);
        Assert.AreEqual(9, settings!["retentionDays"]!.GetValue<int>());
        Assert.AreEqual("Asia/Tokyo", settings["timeZone"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SchedulesInWhicheverOfTwoConcurrentTimeZoneChangesWasSavedLast()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add((await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>());
        }

        var responses = await Task.WhenAll(ChangeTimeZone(client, "Asia/Tokyo"), ChangeTimeZone(client, "America/New_York"));

        Assert.IsTrue(responses.All(response => response.IsSuccessStatusCode));
        var expected = await TimeZone(client) == "Asia/Tokyo" ? At("2026-10-20T00:00:00Z") : At("2026-10-19T13:00:00Z");
        foreach (var id in ids)
        {
            Assert.AreEqual(expected, await NextRunAt(client, id));
        }
    }

    [TestMethod]
    public async Task AScheduleChangedWhileTheTimeZoneChangesIsScheduledInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        HttpClient? client = null;
        // The schedule change's update, which sets the next Run only if the Watch isn't paused.
        var changeTimeZone = new BeforeCommand("CASE", async () => (await ChangeTimeZone(client!, "Asia/Tokyo")).EnsureSuccessStatusCode());
        await using var host = WithInterceptor(factory, changeTimeZone);
        client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/watches/{id}/schedule", Daily("10:00"), testContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.IsTrue(changeTimeZone.Fired);
        // 10:00 in Tokyo is 01:00 UTC.
        Assert.AreEqual(At("2026-10-20T01:00:00Z"), await NextRunAt(client, id));
        client.Dispose();
    }

    [TestMethod]
    public async Task AWatchResumedWhileTheTimeZoneChangesIsScheduledInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        HttpClient? client = null;
        var changeTimeZone = new BeforeCommand("AND \"w\".\"IsPaused\"",
            async () => (await ChangeTimeZone(client!, "Asia/Tokyo")).EnsureSuccessStatusCode());
        await using var host = WithInterceptor(factory, changeTimeZone);
        client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("09:00")))["id"]!.GetValue<string>();
        await PostWatchAction(client, id, "pause");

        await PostWatchAction(client, id, "resume");

        Assert.IsTrue(changeTimeZone.Fired);
        Assert.AreEqual(At("2026-10-20T00:00:00Z"), await NextRunAt(client, id));
        client.Dispose();
    }

    [TestMethod]
    public async Task ARunClaimedWhileTheTimeZoneChangesLeavesTheNextRunInTheNewTimeZone()
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        factory.Runner.DefaultResult = BlockingRunner.Succeeded;
        HttpClient? client = null;
        // The claim's update, which records when the scheduled Run started.
        var changeTimeZone = new BeforeCommand("\"LastScheduledRunAt\" = @",
            async () => (await ChangeTimeZone(client!, "Asia/Tokyo")).EnsureSuccessStatusCode());
        await using var host = WithInterceptor(factory, changeTimeZone);
        client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, Daily("12:05")))["id"]!.GetValue<string>();
        factory.Time.Advance(TimeSpan.FromMinutes(5));

        await StartAndFinishDueRuns(host);

        Assert.IsTrue(changeTimeZone.Fired);
        // The change scheduled the Watch from now in Tokyo, so the Run it superseded doesn't start.
        Assert.IsEmpty(await History(client, id));
        Assert.AreEqual(At("2026-10-20T03:05:00Z"), await NextRunAt(client, id));
        client.Dispose();
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("PUT")]
    public async Task RequiresALogin(string method)
    {
        await using var factory = new LookoutWebApplicationFactory { PollsSchedule = false };
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/settings/time-zone")
        {
            Content = JsonContent.Create(new { timeZone = "Asia/Tokyo" })
        };

        var response = await client.SendAsync(request, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string NewDatabasePath() => Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Daily(string timeOfDay, int jitterMinutes = 0) => new()
    {
        ["scheduleKind"] = "daily",
        ["timeOfDay"] = timeOfDay,
        ["jitterMinutes"] = jitterMinutes.ToString(CultureInfo.InvariantCulture)
    };

    private static Dictionary<string, string> Cron(string cron) => new() { ["scheduleKind"] = "cron", ["cron"] = cron };

    private static WebApplicationFactory<Program> WithInterceptor(LookoutWebApplicationFactory factory, BeforeCommand interceptor) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<LookoutDbContext>(options => options.AddInterceptors(interceptor))));

    private Task<HttpResponseMessage> ChangeTimeZone(HttpClient client, string? timeZone) =>
        client.PutAsJsonAsync("/api/settings/time-zone", new { timeZone }, testContext.CancellationToken);

    private async Task<string> TimeZone(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken))!["timeZone"]!.GetValue<string>();

    private async Task<JsonObject> CreateWatch(HttpClient client, Dictionary<string, string> schedule)
    {
        var response = await PostWatch(client, schedule);
        Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(testContext.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    /// <summary>Posts a new Watch with <paramref name="fields"/>, its Schedule and any others, such as its time zone.</summary>
    private async Task<HttpResponseMessage> PostWatch(HttpClient client, Dictionary<string, string> fields)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default {}"), "script", "price.ts" }
        };
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }
        return await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
    }

    private async Task PostWatchAction(HttpClient client, string id, string action)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/{action}", UriKind.Relative), null, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonObject> Watch(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken))!;

    private async Task<DateTimeOffset?> NextRunAt(HttpClient client, string id) =>
        (await Watch(client, id))["nextRunAt"]?.GetValue<DateTimeOffset>();

    private async Task<JsonArray> History(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken))!;

    private Task<IReadOnlyList<Admission>> StartDueRuns(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<Scheduler>().StartDueRunsAsync(testContext.CancellationToken);

    private async Task StartAndFinishDueRuns(WebApplicationFactory<Program> host) =>
        await Task.WhenAll((await StartDueRuns(host)).OfType<Admitted>().Select(admitted => admitted.Finished));
}
