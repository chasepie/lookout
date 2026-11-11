using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// Cron expressions as the scheduler reads them, so the frontend never has to read them itself.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CronController(TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// How many runs <see cref="Preview"/> lists.
    /// </summary>
    public const int PreviewRunCount = 5;

    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Whether a watch could be scheduled with the expression, and if so when its next runs would be, counting from now
    /// in the server's time zone, as the scheduler counts them.
    /// </summary>
    [HttpGet("preview")]
    public CronPreview Preview(string expression)
    {
        var timeZone = TimeZoneInfo.Local;
        if (!CronSchedules.TryParse(expression, out var cron))
        {
            return new CronPreview(false, [], timeZone.Id);
        }

        var nextRuns = cron
            .GetOccurrences(_timeProvider.GetUtcNow().UtcDateTime, DateTime.MaxValue, timeZone, fromInclusive: false)
            .Take(PreviewRunCount)
            .ToList();
        return new CronPreview(true, nextRuns, timeZone.Id);
    }
}

/// <param name="IsValid">Whether a watch can be saved with the expression.</param>
/// <param name="NextRuns">
/// The next <see cref="CronController.PreviewRunCount"/> runs, in UTC; fewer, or none, if the expression has no more.
/// </param>
/// <param name="TimeZone">The time zone the expression's times are in: the server's.</param>
public sealed record CronPreview(bool IsValid, IReadOnlyList<DateTime> NextRuns, string TimeZone);
