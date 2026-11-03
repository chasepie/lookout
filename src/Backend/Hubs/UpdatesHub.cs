using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.SignalR;

namespace ChasePie.Lookout.Backend.Hubs;

/// <summary>
/// Pushes watch and run changes to every connected browser. Clients only listen; there's one user,
/// so every client gets every message. Send through <see cref="UpdatesNotifier"/>.
/// </summary>
public class UpdatesHub : Hub<IUpdatesClient>
{
    public const string Path = "/hubs/updates";
}

/// <summary>
/// The messages clients receive. Method names are the message names the frontend listens for.
/// </summary>
public interface IUpdatesClient
{
    /// <summary>
    /// A run was created or changed status.
    /// </summary>
    Task RunUpdated(WatchRunUpdate update);

    /// <summary>
    /// A watch was created, edited, deleted, or had its script replaced.
    /// </summary>
    Task WatchChanged(int watchId);
}

/// <summary>
/// A run's state, without its content or screenshot. Clients fetch the full run if they need it.
/// </summary>
public sealed record WatchRunUpdate(
    int Id,
    int WatchId,
    WatchRunStatus Status,
    DateTime CreatedTime,
    DateTime? StartTime,
    DateTime? EndTime)
{
    public static WatchRunUpdate From(WatchRun watchRun) => new(
        watchRun.Id,
        watchRun.WatchId,
        watchRun.Status,
        watchRun.CreatedTime,
        watchRun.StartTime,
        watchRun.EndTime);
}
