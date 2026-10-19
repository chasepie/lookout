using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Runner;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed class FakeRunnerClient : IRunnerClient
{
    public ConcurrentQueue<RunnerResult> Results { get; } = new();
    public RunnerResult? DefaultResult { get; set; }
    public Func<RunnerRequest, CancellationToken, Task<RunnerResult>>? OnExecute { get; set; }

    public Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken)
    {
        if (OnExecute is not null)
        {
            return OnExecute(request, cancellationToken);
        }
        return Task.FromResult(Results.TryDequeue(out var result)
            ? result : DefaultResult ?? throw new InvalidOperationException("No runner result was arranged for this Run."));
    }
}
