using ChasePie.Lookout.Backend.IntegrationTests;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Frontend.TestHost;

public static class Program
{
    public static async Task Main()
    {
        // Real time, so the browser shows next Runs relative to now; no flow waits the 3 minutes a scheduled Run needs.
        await using var factory = new LookoutWebApplicationFactory { UseSystemTime = true };
        factory.Runner.DefaultResult = new RunnerResult
        {
            Outcome = RunOutcome.Succeeded, Content = "£19.99", Details = "In stock", DurationMs = 25
        };
        factory.Runner.OnExecute = (request, _) => Task.FromResult(request.Context.NotificationUrl switch
        {
            "https://example.com/known-failure" => new RunnerResult { Outcome = RunOutcome.KnownFailure, Details = "Access denied", DurationMs = 25 },
            "https://example.com/errored" => new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "Page unavailable", DurationMs = 25 },
            _ => factory.Runner.DefaultResult
        });
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
