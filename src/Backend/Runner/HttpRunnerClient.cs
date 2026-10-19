using System.Text.Json;
using System.Text.Json.Serialization;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Runner;

public sealed class HttpRunnerClient(HttpClient client, TimeProvider timeProvider) : IRunnerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<RunOutcome>(JsonNamingPolicy.CamelCase, false) }
    };

    public async Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            using var response = await client.PostAsJsonAsync(new Uri("/execute", UriKind.Relative), request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<RunnerResult>(JsonOptions, cancellationToken)
                ?? throw new JsonException("Runner returned an empty result.");
            if (!double.IsFinite(result.DurationMs) || result.DurationMs < 0
                || (result.Outcome == RunOutcome.Succeeded && result.Content is null))
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
