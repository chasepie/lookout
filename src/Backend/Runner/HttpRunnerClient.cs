using System.Text.Json;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Runner;

public sealed class HttpRunnerClient(HttpClient client, TimeProvider timeProvider) : IRunnerClient
{
    public async Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            using var response = await client.PostAsJsonAsync(new Uri("execute", UriKind.Relative), request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"Runner returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                try
                {
                    using var errorBody = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
                    if (errorBody?.RootElement.ValueKind == JsonValueKind.Object
                        && errorBody.RootElement.TryGetProperty("error", out var detail)
                        && detail.ValueKind == JsonValueKind.String)
                    {
                        message += $" {detail.GetString()}";
                    }
                }
                catch (JsonException)
                {
                    // Proxies can return an HTML or empty error response; keep the HTTP status.
                }
                throw new HttpRequestException(message, null, response.StatusCode);
            }
            var result = await response.Content.ReadFromJsonAsync<RunnerResult>(cancellationToken)
                ?? throw new JsonException("Runner returned an empty result.");
            if (!double.IsFinite(result.DurationMs) || result.DurationMs < 0)
            {
                throw new JsonException("Runner returned an invalid result.");
            }
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new RunnerResult
            {
                Outcome = RunOutcome.Errored,
                ErrorMessage = $"Runner could not complete the Run: {error.Message}",
                DurationMs = timeProvider.GetElapsedTime(started).TotalMilliseconds
            };
        }
    }
}
