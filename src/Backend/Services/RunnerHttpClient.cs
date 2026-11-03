using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Typed HTTP client for the Runner service's <c>WatchRunController</c>.
/// </summary>
public class RunnerHttpClient(HttpClient httpClient)
{
    internal const string LOOKOUT_RUNNER_URL = "LOOKOUT_RUNNER_URL";

    /// <summary>
    /// Asks the Runner to execute the named watch script and returns its result.
    /// </summary>
    public async Task<WatchRunResult> RunWatchScriptAsync(string scriptName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);

        var requestUri = $"WatchRun?scriptName={Uri.EscapeDataString(scriptName)}";
        return await httpClient.GetFromJsonAsync<WatchRunResult>(requestUri, cancellationToken)
            ?? throw new InvalidOperationException("Runner returned an empty response.");
    }

    /// <summary>
    /// Configures the client's base address from the <c>LOOKOUT_RUNNER_URL</c> environment variable.
    /// </summary>
    internal static void Configure(IServiceProvider services, HttpClient client)
    {
        var runnerUrl = services.GetRequiredService<IConfiguration>()[LOOKOUT_RUNNER_URL];
        if (string.IsNullOrWhiteSpace(runnerUrl))
        {
            throw new InvalidOperationException($"Environment variable {LOOKOUT_RUNNER_URL} must be set");
        }

        // Ensure a trailing slash so relative request URIs append to any path in the base URL.
        if (!runnerUrl.EndsWith('/'))
        {
            runnerUrl += "/";
        }

        if (!Uri.TryCreate(runnerUrl, UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException($"Environment variable {LOOKOUT_RUNNER_URL} must be an absolute URL");
        }

        client.BaseAddress = baseAddress;
    }
}
