using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace ChasePie.Lookout.Backend.Events;

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
