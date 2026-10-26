using System.Net;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <param name="RetentionDays">How many days a Run is kept before it is pruned, unless it is a Change.</param>
/// <param name="TimeZone">The install's saved IANA time zone.</param>
/// <param name="EffectiveTimeZone">
/// The time zone daily and cron Schedules use: the saved one, or the one Lookout falls back to while this host doesn't
/// know it.
/// </param>
public sealed record SettingsResponse(int RetentionDays, string? TimeZone, string EffectiveTimeZone);

public sealed class SettingsRequest
{
    public required int RetentionDays { get; init; }
}

public sealed class TimeZoneRequest
{
    /// <summary>An IANA time zone, such as Europe/London.</summary>
    public string? TimeZone { get; init; }
}

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(LookoutDbContext database, WatchSchedules schedules) : LookoutControllerBase
{
    [HttpGet]
    public async Task<SettingsResponse> Get(CancellationToken cancellationToken)
    {
        var settings = await database.Settings.AsNoTracking()
            .Select(settings => new { settings.RetentionDays, settings.TimeZone })
            .SingleAsync(cancellationToken);
        return new SettingsResponse(settings.RetentionDays, settings.TimeZone,
            schedules.EffectiveTimeZone(settings.TimeZone));
    }

    [HttpPut]
    public async Task<ActionResult<SettingsResponse>> Change(SettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.RetentionDays is < Settings.MinRetentionDays or > Settings.MaxRetentionDays)
        {
            return Problem(
                detail: $"Keep Runs for between {Settings.MinRetentionDays} and {Settings.MaxRetentionDays} days.",
                statusCode: HttpStatusCode.BadRequest);
        }

        var updated = await database.Settings.ExecuteUpdateAsync(setters => setters
            .SetProperty(settings => settings.RetentionDays, request.RetentionDays), cancellationToken);
        // Migrations create the only row, so a database without one isn't Lookout's, and nothing could be saved.
        if (updated != 1)
        {
            return Problem(
                detail: "Lookout's settings are missing from its database, so they could not be saved.",
                statusCode: HttpStatusCode.InternalServerError);
        }
        return await Get(cancellationToken);
    }

    /// <summary>
    /// Changes the install's time zone, rescheduling daily and cron Watches in it. Saved, it outlasts restarts and
    /// whatever <c>Scheduling:TimeZone</c> says.
    /// </summary>
    [HttpPut("time-zone")]
    public async Task<ActionResult<SettingsResponse>> ChangeTimeZone(TimeZoneRequest request,
        CancellationToken cancellationToken)
    {
        var update = await schedules.ChangeTimeZoneAsync(request.TimeZone, cancellationToken);
        if (update.Status == ScheduleUpdateStatus.Invalid)
        {
            return Problem(detail: update.Problem!, statusCode: HttpStatusCode.BadRequest);
        }
        return await Get(cancellationToken);
    }
}
