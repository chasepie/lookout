using System.Linq.Expressions;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record NotificationChannelSummary(Guid Id, string Name, NotificationChannelKind Kind)
{
    /// <summary>Maps a Notification channel in a database query, leaving its secrets behind; <see cref="From"/> does the same in memory.</summary>
    public static readonly Expression<Func<NotificationChannel, NotificationChannelSummary>> Projection =
        notificationChannel => new NotificationChannelSummary(notificationChannel.Id, notificationChannel.Name, notificationChannel.Kind);

    private static readonly Func<NotificationChannel, NotificationChannelSummary> CompiledProjection = Projection.Compile();

    public static NotificationChannelSummary From(NotificationChannel notificationChannel) => CompiledProjection(notificationChannel);
}

public sealed class NotificationChannelRequest
{
    public string? Name { get; init; }
    public NotificationChannelKind? Kind { get; init; }
    public string? WebhookUrl { get; init; }
    public string? UserKey { get; init; }
    public string? AppToken { get; init; }
}
