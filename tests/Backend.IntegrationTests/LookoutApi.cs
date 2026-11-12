using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The API calls tests set things up with, each checking it worked.
/// </summary>
internal static class LookoutApi
{
    public const string ApplicationToken = "azGDORePK8gMaC0QOYAMyEEuzJnyUi";
    public const string UserKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";

    public static async Task<NotificationChannelSummary> CreateChannelAsync(
        this HttpClient client, string name, CancellationToken cancellationToken, int retrySeconds = 60, int expireSeconds = 3600)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/NotificationChannels",
            new
            {
                Name = name,
                Service = NotificationService.Pushover,
                Pushover = new { ApplicationToken, UserKey, RetrySeconds = retrySeconds, ExpireSeconds = expireSeconds },
            },
            cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        return (await response.Content.ReadFromJsonAsync<NotificationChannelSummary>(cancellationToken))!;
    }

    /// <param name="fields">More of the watch's fields, as form fields, such as <c>KnownFailureThreshold</c>.</param>
    public static async Task<int> CreateWatchAsync(
        this HttpClient client, string name, CancellationToken cancellationToken, params (string Name, string Value)[] fields)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(name), "Name" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("export default {};")), "script", "watch.ts" },
        };
        foreach (var (fieldName, value) in fields)
        {
            form.Add(new StringContent(value), fieldName);
        }

        using var response = await client.PostAsync("/api/Watches", form, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        return (await response.Content.ReadFromJsonAsync<Watch>(cancellationToken))!.Id;
    }

    public static Task<HttpResponseMessage> PutSubscriptionsAsync(
        this HttpClient client, int watchId, CancellationToken cancellationToken, params object[] subscriptions) =>
        client.PutAsJsonAsync($"/api/Watches/{watchId}/subscriptions", subscriptions, cancellationToken);

    /// <summary>
    /// Replaces the watch's subscriptions, checking it worked.
    /// </summary>
    public static async Task SubscribeAsync(
        this HttpClient client, int watchId, CancellationToken cancellationToken, params object[] subscriptions)
    {
        using var response = await client.PutSubscriptionsAsync(watchId, cancellationToken, subscriptions);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Creates a run of the watch and waits for the worker to finish it.
    /// </summary>
    public static async Task<WatchRun> RunAsync(this HttpClient client, int watchId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"/api/Watches/{watchId}/runs", null, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        var runId = (await response.Content.ReadFromJsonAsync<WatchRun>(cancellationToken))!.Id;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = await client.GetFromJsonAsync<WatchRun>($"/api/Watches/{watchId}/runs/{runId}", timeout.Token);
            if (run!.Status == WatchRunStatus.Done)
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    /// <summary>
    /// Creates a run of the watch, without waiting for the worker.
    /// </summary>
    public static async Task QueueRunAsync(this HttpClient client, int watchId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"/api/Watches/{watchId}/runs", null, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// The watch's runs, oldest first.
    /// </summary>
    public static async Task<List<WatchRunSummary>> GetRunsAsync(
        this HttpClient client, int watchId, CancellationToken cancellationToken)
    {
        var newestFirst = await client.GetFromJsonAsync<List<WatchRunSummary>>(
            $"/api/Watches/{watchId}/runs", cancellationToken);
        return [.. newestFirst!.OrderBy(r => r.Id)];
    }

    /// <summary>
    /// Waits for the watch to have <paramref name="count"/> runs, all done, and returns them oldest first.
    /// </summary>
    public static async Task<List<WatchRunSummary>> WaitForRunsAsync(
        this HttpClient client, int watchId, int count, CancellationToken cancellationToken)
    {
        List<WatchRunSummary> runs = [];
        await WaitUntilAsync(async () =>
        {
            runs = await client.GetRunsAsync(watchId, cancellationToken);
            return runs.Count >= count && runs.All(r => r.Status == WatchRunStatus.Done);
        }, cancellationToken);
        return runs;
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, checking it every 50 ms for up to a minute.
    /// </summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}
