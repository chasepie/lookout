using System.Linq.Expressions;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record NotificationChannelSummary(Guid Id, string Name, NotificationChannelKind Kind)
{
    /// <summary>Maps a Notification channel in a database query, leaving its secrets behind; <see cref="From"/> does the same in memory.</summary>
    public static readonly Expression<Func<NotificationChannel, NotificationChannelSummary>> Projection =
        nc => new NotificationChannelSummary(nc.Id, nc.Name, nc.Kind);

    private static readonly Func<NotificationChannel, NotificationChannelSummary> CompiledProjection = Projection.Compile();

    public static NotificationChannelSummary From(NotificationChannel nc) => CompiledProjection(nc);
}

public sealed class NotificationChannelRequest
{
    public string? Name { get; init; }
    public NotificationChannelKind? Kind { get; init; }
    public string? WebhookUrl { get; init; }
    public string? UserKey { get; init; }
    public string? AppToken { get; init; }
}
