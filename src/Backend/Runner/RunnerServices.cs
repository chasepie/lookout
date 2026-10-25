using System.Net.Http.Headers;

namespace ChasePie.Lookout.Backend.Runner;

public static class RunnerServices
{
    /// <summary>
    /// The runner at <c>Runner:BaseUrl</c>, which executes Scripts (ADR-0001), sent <c>Runner:Token</c>, if set, as a
    /// bearer token with every request. A runner anywhere but this machine requires one.
    /// </summary>
    public static void AddRunner(this IServiceCollection services, IConfiguration configuration)
    {
        // The address the runner listens on by default. localhost could reach whatever else holds port 3000 on ::1.
        var baseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Runner:BaseUrl", "http://127.0.0.1:3000");
        var token = ConfiguredTokens.Read(configuration, "Runner:Token");
        // The runner refuses to start without one there, so this is a mistake, such as a deployment's unset token.
        if (token is null && !baseAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Runner:Token must be set, such as from openssl rand -hex 32, since the runner at {baseAddress} isn't on this machine");
        }

        services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
        {
            client.BaseAddress = baseAddress;
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            // HttpRunnerClient sets each request's deadline from its Script timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
    }
}
