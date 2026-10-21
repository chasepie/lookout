using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Holds the first command that writes to <paramref name="table"/> until the test releases it, and records when
/// another command writes to it meanwhile, so a test can overlap two requests' writes.
/// </summary>
public sealed class HoldFirstWriteInterceptor(string table) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writes;

    public TaskCompletionSource FirstHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OtherArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    private async Task HoldAsync(DbCommand command)
    {
        var text = command.CommandText.TrimStart();
        var writes = (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            && text.Contains($"\"{table}\"", StringComparison.Ordinal);
        if (!writes)
        {
            return;
        }

        if (Interlocked.Increment(ref _writes) == 1)
        {
            FirstHeld.TrySetResult();
            await _released.Task;
        }
        else
        {
            OtherArrived.TrySetResult();
        }
    }
}
