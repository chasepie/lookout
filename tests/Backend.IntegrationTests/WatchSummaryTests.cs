using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The watch and run lists the frontend's pages are built from.
/// </summary>
[TestClass]
public sealed class WatchSummaryTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    private static readonly byte[] Screenshot = [1, 2, 3];
    private static readonly string?[] NewestThree = ["run 5", "run 4", "run 3"];
    private static readonly string?[] OldestTwo = ["run 2", "run 1"];
    private static readonly string[] SortedNames = ["apple", "Banana", "Écluse", "zebra"];

    private static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task RunListsLeaveOutScreenshots()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 2, screenshot: Screenshot);

        using var response = await client.GetAsync($"/api/Watches/{watchId}/runs", Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        var runs = JsonSerializer.Deserialize<List<WatchRunSummary>>(json, JsonSerializerOptions.Web)!;

        Assert.IsFalse(json.Contains("\"screenshot\"", StringComparison.Ordinal));
        Assert.IsTrue(runs.TrueForAll(r => r.HasScreenshot));
        var run = await client.GetFromJsonAsync<WatchRun>($"/api/Watches/{watchId}/runs/{runs[0].Id}", Ct);
        Assert.AreSequenceEqual(Screenshot, run!.Screenshot!);
    }

    [TestMethod]
    public async Task RunsPageBackFromNewest()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 5);

        var first = await client.GetFromJsonAsync<List<WatchRunSummary>>($"/api/Watches/{watchId}/runs?limit=3", Ct);
        var rest = await client.GetFromJsonAsync<List<WatchRunSummary>>(
            $"/api/Watches/{watchId}/runs?limit=3&before={first![^1].Id}", Ct);

        Assert.AreSequenceEqual(NewestThree, first.ConvertAll(r => r.Content));
        Assert.AreSequenceEqual(OldestTwo, rest!.ConvertAll(r => r.Content));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(WatchRunsController.MaxPageSize + 1)]
    public async Task RunPagesHaveALimit(int limit)
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 1);

        using var response = await client.GetAsync($"/api/Watches/{watchId}/runs?limit={limit}", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task WatchesComeWithTheirRecentRunsNewestFirst()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: WatchSummary.RecentRunCount + 2);

        var watches = await client.GetFromJsonAsync<List<WatchSummary>>("/api/Watches", Ct);

        var recent = watches!.Single().RecentRuns;
        Assert.HasCount(WatchSummary.RecentRunCount, recent);
        Assert.AreEqual($"run {WatchSummary.RecentRunCount + 2}", recent[0].Content);
    }

    [TestMethod]
    public async Task OneWatchComesWithItsRecentRunsOnlyWhenAsked()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 5);

        var plain = await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct);
        var withRuns = await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}?recentRuns=3", Ct);
        using var tooMany = await client.GetAsync(
            $"/api/Watches/{watchId}?recentRuns={WatchSummary.RecentRunCount + 1}", Ct);

        Assert.IsEmpty(plain!.RecentRuns);
        Assert.AreSequenceEqual(NewestThree, withRuns!.RecentRuns.Select(r => r.Content).ToList());
        Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [TestMethod]
    public async Task AScheduledWatchSaysWhenItNextRuns()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        // An hour after the last run, which is in the future so the scheduler leaves it alone
        var lastRun = DateTime.UtcNow.AddMinutes(5);
        var watchId = await SeedAsync(factory, runCount: 1, scheduled: true, lastRunTime: lastRun);

        var watch = await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct);

        Assert.AreEqual(lastRun.AddHours(1), watch!.NextRunTime);
    }

    [TestMethod]
    public async Task AnUnscheduledWatchHasNoNextRun()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 1);

        var watch = await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct);

        Assert.IsNull(watch!.NextRunTime);
    }

    [TestMethod]
    public async Task WatchesAreListedByNameIgnoringCaseAndAccents()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        foreach (var name in new[] { "zebra", "Écluse", "apple", "Banana" })
        {
            await SeedAsync(factory, runCount: 0, name: name);
        }

        var watches = await client.GetFromJsonAsync<List<WatchSummary>>("/api/Watches", Ct);

        Assert.AreSequenceEqual(SortedNames, watches!.ConvertAll(w => w.Name));
    }

    [TestMethod]
    [DataRow("/api/Watches/abc")]
    [DataRow("/api/Watches/999")]
    [DataRow("/api/Watches/999/runs")]
    public async Task AWatchThatDoesNotExistIsNotFound(string url)
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        using var response = await client.GetAsync(url, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task AWatchWithNoRunsHasAnEmptyLog()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SeedAsync(factory, runCount: 0);

        var runs = await client.GetFromJsonAsync<List<WatchRunSummary>>($"/api/Watches/{watchId}/runs", Ct);

        Assert.IsEmpty(runs!);
    }

    /// <summary>
    /// Adds a watch with finished runs, a minute apart and numbered in their content, so the worker leaves them be.
    /// </summary>
    private async Task<int> SeedAsync(
        LookoutFactory factory,
        int runCount,
        byte[]? screenshot = null,
        bool scheduled = false,
        DateTime? lastRunTime = null,
        string name = "Prices")
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var watch = new Watch
        {
            Name = name,
            ScheduleEnabled = scheduled,
            ScheduleKind = scheduled ? WatchScheduleKind.Hourly : null,
            ScheduleInterval = scheduled ? "1" : null,
            ScriptFileId = scheduled ? Guid.NewGuid() : null,
        };
        var last = lastRunTime ?? Start.AddMinutes(runCount);
        for (var i = 1; i <= runCount; i++)
        {
            watch.WatchRuns.Add(new WatchRun
            {
                WatchId = 0,
                CreatedTime = last.AddMinutes(i - runCount),
                Status = WatchRunStatus.Done,
                Content = $"run {i}",
                Screenshot = screenshot,
            });
        }

        dbContext.Watches.Add(watch);
        await dbContext.SaveChangesAsync(Ct);
        return watch.Id;
    }
}
