using System.Linq.Expressions;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record WatchSummary(
    Guid Id,
    string Name,
    string NotificationUrl,
    Guid? CurrentScriptVersionId,
    string? Baseline,
    WatchStatus Status,
    bool IsPaused,
    bool UsesVpn,
    int FailureStreak,
    int TimeoutSeconds,
    Run? LastRun,
    Schedule Schedule,
    int JitterMinutes,
    DateTimeOffset? NextRunAt,
    IReadOnlyList<Guid> NotificationChannelIds)
{
    /// <summary>Maps a Watch in a database query; <see cref="From"/> applies the same mapping in memory.</summary>
    public static readonly Expression<Func<Watch, WatchSummary>> Projection = WatchStatuses.WithStatus((watch, status) => new WatchSummary(
        watch.Id,
        watch.Name,
        watch.NotificationUrl,
        watch.CurrentScriptVersionId,
        watch.Baseline,
        status,
        watch.IsPaused,
        watch.UsesVpn,
        watch.FailureStreak,
        watch.TimeoutSeconds,
        watch.Runs.OrderByDescending(run => run.Id).FirstOrDefault(),
        watch.Schedule,
        watch.JitterMinutes,
        watch.NextRunAt,
        watch.NotificationChannels.Select(notificationChannel => notificationChannel.Id).ToList()));

    /// <summary>Whether the Watch is Running, which is shown alongside its <see cref="Status"/>.</summary>
    public bool IsRunning { get; init; }

    private static readonly Func<Watch, WatchSummary> CompiledProjection = Projection.Compile();

    public static WatchSummary From(Watch watch) => CompiledProjection(watch);
}

public sealed class NotificationChannelsRequest
{
    public required IReadOnlyList<Guid> NotificationChannelIds { get; init; }
}

public sealed class EditWatchRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required int TimeoutSeconds { get; init; }
    /// <summary>Required, so a client that leaves it out can't send a VPN Watch's Runs from the home IP.</summary>
    public required bool UsesVpn { get; init; }
}

public sealed class CreateWatchRequest : ScheduleRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required IFormFile Script { get; init; }
    public List<Guid> NotificationChannelIds { get; init; } = [];
    /// <summary>Whether the Watch's Runs go through the VPN rather than direct.</summary>
    public bool UsesVpn { get; init; }
}

public class ScheduleRequest
{
    public ScheduleKind? ScheduleKind { get; init; }
    /// <summary>For an interval schedule, how many <see cref="Unit"/>s apart its Runs are.</summary>
    public int? Every { get; init; }
    public IntervalUnit? Unit { get; init; }
    /// <summary>For a daily schedule, the time of day in the install's time zone.</summary>
    public TimeOnly? TimeOfDay { get; init; }
    public string? Cron { get; init; }
    public int JitterMinutes { get; init; }
}
