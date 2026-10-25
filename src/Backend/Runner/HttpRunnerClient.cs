using System.Text.Json;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Runner;

public sealed class HttpRunnerClient(HttpClient client, TimeProvider timeProvider) : IRunnerClient
{
    /// <summary>The runner allows five seconds to capture a timeout screenshot, plus transport overhead.</summary>
    public static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(15);

    public async Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var deadline = TimeSpan.FromMilliseconds(request.TimeoutMs) + ResponseGrace;
        using var timeout = new CancellationTokenSource(deadline, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await client.PostAsJsonAsync(
                requestUri: new Uri("execute", UriKind.Relative),
                value: request,
                cancellationToken: linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"Runner returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                try
                {
                    using var errorBody = await response.Content.ReadFromJsonAsync<JsonDocument>(linked.Token);
                    if (errorBody?.RootElement.ValueKind == JsonValueKind.Object
                        && errorBody.RootElement.TryGetProperty("error", out var detail)
                        && detail.ValueKind == JsonValueKind.String)
                    {
                        message += $" {detail.GetString()}";
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Proxies can return an HTML, empty or oddly encoded error response; keep the HTTP status.
                }
                throw new HttpRequestException(message, null, response.StatusCode);
            }
            var result = await response.Content.ReadFromJsonAsync<RunnerResult>(linked.Token)
                ?? throw new JsonException("Runner returned an empty result.");
            if (!double.IsFinite(result.DurationMs) || result.DurationMs < 0)
            {
                throw new JsonException("Runner returned an invalid result.");
            }
            return result;
        }
        // Anything but the caller cancelling is a runner outage or a response Lookout can't read.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            var reason = timeout.IsCancellationRequested
                ? $"the runner did not respond within {deadline.TotalSeconds:0} seconds."
                : error.Message;
            return new RunnerResult
            {
                Outcome = RunOutcome.Errored,
                ErrorMessage = $"Runner could not complete the Run: {reason}",
                DurationMs = timeProvider.GetElapsedTime(started).TotalMilliseconds
            };
        }
    }
}
