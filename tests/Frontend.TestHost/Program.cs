using ChasePie.Lookout.Backend.IntegrationTests;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Frontend.TestHost;

public static class Program
{
    public static async Task Main()
    {
        // Real time, so the browser shows next Runs relative to now; no flow waits the 3 minutes a scheduled Run needs.
        // Channels deliver for real, so a test can receive a notification at its own endpoint. Pushover's go to a local
        // port nothing listens on (the discard port), so no test reaches the internet.
        Environment.SetEnvironmentVariable("Notifications__PushoverBaseUrl", "http://127.0.0.1:9/");
        await using var factory = new LookoutWebApplicationFactory { UseSystemTime = true, UseRealChannels = true };
        factory.Runner.DefaultResult = new RunnerResult
        {
            Outcome = RunOutcome.Succeeded, Content = "£19.99", Details = "In stock", DurationMs = 25
        };
        var changingRuns = 0;
        var slowRuns = 0;
        factory.Runner.OnExecute = async (request, cancellationToken) => (await Execute(request, cancellationToken)) with
        {
            // Test runs ask for a screenshot whatever the outcome: a single transparent pixel.
            Screenshot = request.AlwaysScreenshot
                ? "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="
                : null
        };
        async Task<RunnerResult> Execute(RunnerRequest request, CancellationToken cancellationToken)
        {
            if (request.Context.NotificationUrl == "https://example.com/slow-changing")
            {
                // Slow enough for a browser to see the Run in progress.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return new RunnerResult
                {
                    Outcome = RunOutcome.Succeeded, Content = $"£{50 - Interlocked.Increment(ref slowRuns)}", DurationMs = 2000
                };
            }
            return request.Context.NotificationUrl switch
            {
                "https://example.com/changing" => new RunnerResult
                {
                    Outcome = RunOutcome.Succeeded, Content = $"£{20 - Interlocked.Increment(ref changingRuns)}", DurationMs = 25
                },
                "https://example.com/known-failure" => new RunnerResult { Outcome = RunOutcome.KnownFailure, Details = "Access denied", DurationMs = 25 },
                "https://example.com/errored" => new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "Page unavailable", DurationMs = 25 },
                _ => factory.Runner.DefaultResult!
            };
        }
        factory.UseKestrel(5117);
        using var client = factory.CreateClient();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Disposing the factory stops Kestrel and removes its temporary database.
        }
    }
}
