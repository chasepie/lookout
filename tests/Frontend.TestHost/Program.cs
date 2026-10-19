using ChasePie.Lookout.Backend.IntegrationTests;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Frontend.TestHost;

public static class Program
{
    public static async Task Main()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult
        {
            Outcome = RunOutcome.Succeeded, Content = "£19.99", Details = "In stock", DurationMs = 25
        };
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
