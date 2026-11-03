using System.Text.Json;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Typed HTTP client for the Runner service's <c>WatchRunController</c>.
/// </summary>
public class RunnerHttpClient(HttpClient httpClient)
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Asks the Runner to execute the named watch script and returns its result.
    /// </summary>
    public async Task<WatchRunResult> RunWatchScriptAsync(string scriptName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);

        var requestUri = $"WatchRun?scriptName={Uri.EscapeDataString(scriptName)}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"Runner returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<WatchRunResult>(cancellationToken)
            ?? throw new InvalidOperationException("Runner returned an empty response.");
    }

    /// <summary>
    /// Returns the problem details' message from an error response, or the raw body if it has none.
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonSerializerOptions.Web);
            if ((problem?.Detail ?? problem?.Title) is { } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall back to the raw body
        }

        return body;
    }

    /// <summary>
    /// Configures the client's base address from the <c>LOOKOUT_RUNNER_URL</c> environment variable.
    /// </summary>
    internal static void Configure(IServiceProvider services, HttpClient client)
    {
        var runnerUrl = services.GetRequiredService<IConfiguration>()[LookoutEnvironmentVariables.RunnerUrl];
        if (string.IsNullOrWhiteSpace(runnerUrl))
        {
            throw new InvalidOperationException($"Environment variable {LookoutEnvironmentVariables.RunnerUrl} must be set");
        }

        // Ensure a trailing slash so relative request URIs append to any path in the base URL.
        if (!runnerUrl.EndsWith('/'))
        {
            runnerUrl += "/";
        }

        if (!Uri.TryCreate(runnerUrl, UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException($"Environment variable {LookoutEnvironmentVariables.RunnerUrl} must be an absolute URL");
        }

        client.BaseAddress = baseAddress;

        // A run waits for the whole browser session; the 100-second default is too short.
        // On timeout the request is aborted, and the Runner stops the script.
        client.Timeout = RunTimeout;
    }
}
