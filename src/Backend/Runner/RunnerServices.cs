namespace ChasePie.Lookout.Backend.Runner;

public static class RunnerServices
{
    /// <summary>The runner at <c>Runner:BaseUrl</c>, which executes Scripts (ADR-0001).</summary>
    public static void AddRunner(this IServiceCollection services, IConfiguration configuration)
    {
        var baseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Runner:BaseUrl", "http://localhost:3000");
        services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
        {
            client.BaseAddress = baseAddress;
            // HttpRunnerClient sets each request's deadline from its Script timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
    }
}
