using ChasePie.Lookout.Backend.Database;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// The single row holding the Owner's session generation. Every session carries the generation it signed in under.
/// </summary>
public class OwnerSessionState
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    public int Generation { get; set; }
}

/// <summary>
/// Which session generation is current. Signing out moves to the next one, which ends every session, including copies
/// of the cookie. The generation is cached here, so checking a session doesn't query the database.
/// </summary>
public sealed class OwnerSessions(IServiceScopeFactory scopeFactory)
{
    private int? _generation;

    /// <exception cref="InvalidOperationException"><see cref="LoadAsync"/> hasn't run.</exception>
    public int Generation => _generation
        ?? throw new InvalidOperationException($"Call {nameof(LoadAsync)} at startup, after migrating the database.");

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        _generation = await dbContext.OwnerSessionStates
            .Where(s => s.Id == OwnerSessionState.SingletonId)
            .Select(s => s.Generation)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Ends every session by moving to the next generation.
    /// </summary>
    public async Task EndAllAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            // Incremented in the database, so concurrent sign-outs both count
            await dbContext.OwnerSessionStates
                .Where(s => s.Id == OwnerSessionState.SingletonId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Generation, x => x.Generation + 1), cancellationToken);
        }

        await LoadAsync(cancellationToken);
    }
}
