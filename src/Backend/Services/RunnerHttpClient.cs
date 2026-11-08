using System.Net;
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
    /// <param name="useVpn">Whether the browser's traffic goes through the Runner's VPN proxy.</param>
    public async Task<WatchRunResult> RunWatchScriptAsync(
        string scriptName, bool useVpn, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);

        var requestUri = $"WatchRun?scriptName={Uri.EscapeDataString(scriptName)}&useVpn={(useVpn ? "true" : "false")}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = response.StatusCode == HttpStatusCode.Unauthorized
                ? $"it refused the API key. Check that {EnvironmentVariables.LookoutRunnerApiKey} is the same for the "
                    + "Backend and the Runner."
                : await ReadErrorAsync(response, cancellationToken);
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
    /// Configures the client's base address from the <c>LOOKOUT_RUNNER_URL</c> environment variable, and has it send
    /// the <see cref="RunnerApiKey"/> on every request.
    /// </summary>
    internal static void Configure(IServiceProvider services, HttpClient client)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        client.BaseAddress = EnvironmentVariables.GetBaseUrl(configuration, EnvironmentVariables.LookoutRunnerUrl)
            ?? throw new InvalidOperationException($"Environment variable {EnvironmentVariables.LookoutRunnerUrl} must be set");
        client.DefaultRequestHeaders.Add(
            RunnerApiKey.HeaderName,
            services.GetRequiredService<RunnerApiKey>().Value);

        // A run waits for the whole browser session; the 100-second default is too short.
        // On timeout the request is aborted, and the Runner stops the script.
        client.Timeout = RunTimeout;
    }
}
