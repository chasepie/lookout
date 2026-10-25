using System.Net.Http.Headers;

namespace ChasePie.Lookout.Backend.Runner;

public static class RunnerServices
{
    /// <summary>
    /// The runner at <c>Runner:BaseUrl</c>, which executes Scripts (ADR-0001), sent <c>Runner:Token</c>, if set, as a
    /// bearer token with every request.
    /// </summary>
    public static void AddRunner(this IServiceCollection services, IConfiguration configuration)
    {
        var baseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Runner:BaseUrl", "http://localhost:3000");
        var token = configuration["Runner:Token"];
        // A bearer token can hold a few other characters, but these are all one needs, as with the VPN's key.
        if (!string.IsNullOrEmpty(token) && !token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InvalidOperationException("Runner:Token must contain only letters, digits, '-' and '_', such as from openssl rand -hex 32");
        }

        services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
        {
            client.BaseAddress = baseAddress;
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            // HttpRunnerClient sets each request's deadline from its Script timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
    }
}
