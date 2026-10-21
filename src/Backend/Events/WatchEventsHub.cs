using System.Collections.Concurrent;
using ChasePie.Lookout.Database;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Events;

/// <summary>The events Lookout pushes to the app, named as the app listens for them.</summary>
public interface IWatchEventsClient
{
    Task RunStarted(RunStartedEvent message);
    Task RunFinished(RunFinishedEvent message);
    Task WatchStatusChanged(WatchStatusChangedEvent message);
}

/// <summary>
/// Pushes <see cref="WatchEvents"/> to the app as they happen. Like the API, it requires a login, and the
/// connection is negotiated with the request token (see <see cref="RequestForgery.ProtectHub"/>).
/// </summary>
public sealed class WatchEventsHub(EventConnections connections, LookoutDbContext database) : Hub<IWatchEventsClient>
{
    public const string Path = "/api/events";

    public override async Task OnConnectedAsync()
    {
        connections.Add(Context);
        // A logout between authenticating this connection and adding it closed the Session's connections without it.
        var sessionId = SessionCookie.SessionId(Context.User);
        if (sessionId is null || !await database.Sessions.AnyAsync(session => session.Id == sessionId, Context.ConnectionAborted))
        {
            Context.Abort();
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// The open connections to <see cref="WatchEventsHub"/> by the Session that opened them. A connection outlives the
/// request that authenticated it, so logging out closes the Session's connections.
/// </summary>
public sealed class EventConnections
{
    private readonly ConcurrentDictionary<string, (Guid? SessionId, HubCallerContext Context)> _connections = new();

    public void Add(HubCallerContext context) =>
        _connections[context.ConnectionId] = (SessionCookie.SessionId(context.User), context);

    public void Remove(HubCallerContext context) => _connections.TryRemove(context.ConnectionId, out _);

    public void Close(Guid sessionId)
    {
        foreach (var (_, connection) in _connections.Where(item => item.Value.SessionId == sessionId))
        {
            connection.Context.Abort();
        }
    }
}
