using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>Holds each execution open until the test finishes it, and lets the test wait for executions to start.</summary>
public sealed class BlockingRunner
{
    public static readonly RunnerResult Succeeded = new() { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };

    private readonly ConcurrentQueue<TaskCompletionSource<RunnerResult>> _pending = new();
    private readonly Lock _lock = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
    private readonly List<RunnerRequest> _requests = [];

    public int Started
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>The requests executed so far, in the order they started.</summary>
    public IReadOnlyList<RunnerRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public Task<RunnerResult> Execute(RunnerRequest request, CancellationToken cancellationToken)
    {
        var run = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(run);
        lock (_lock)
        {
            _requests.Add(request);
            foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _requests.Count).ToList())
            {
                waiter.Reached.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
        return run.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits until <paramref name="count"/> executions have started, then checks no more have.</summary>
    public async Task WaitForStarted(int count)
    {
        Task reached;
        lock (_lock)
        {
            if (_requests.Count >= count)
            {
                reached = Task.CompletedTask;
            }
            else
            {
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, waiter));
                reached = waiter.Task;
            }
        }
        await reached.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(count, Started);
    }

    public void FinishOne(RunnerResult? result = null)
    {
        Assert.IsTrue(_pending.TryDequeue(out var run), "No execution is in progress.");
        run.SetResult(result ?? Succeeded);
    }

    public void FinishAll()
    {
        while (_pending.TryDequeue(out var run))
        {
            run.SetResult(Succeeded);
        }
    }
}
