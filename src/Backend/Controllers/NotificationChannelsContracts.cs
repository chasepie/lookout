using System.Linq.Expressions;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record ChannelSummary(Guid Id, string Name, ChannelKind Kind)
{
    /// <summary>Maps a Channel in a database query, leaving its secrets behind; <see cref="From"/> does the same in memory.</summary>
    public static readonly Expression<Func<Channel, ChannelSummary>> Projection =
        channel => new ChannelSummary(channel.Id, channel.Name, channel.Kind);

    private static readonly Func<Channel, ChannelSummary> CompiledProjection = Projection.Compile();

    public static ChannelSummary From(Channel channel) => CompiledProjection(channel);
}

public sealed class ChannelRequest
{
    public string? Name { get; init; }
    public ChannelKind? Kind { get; init; }
    public string? WebhookUrl { get; init; }
    public string? UserKey { get; init; }
    public string? AppToken { get; init; }
}
