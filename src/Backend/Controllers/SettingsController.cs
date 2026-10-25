using System.Net;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record SettingsResponse(int RetentionDays);

public sealed class SettingsRequest
{
    public required int RetentionDays { get; init; }
}

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(LookoutDbContext database) : LookoutControllerBase
{
    [HttpGet]
    public async Task<SettingsResponse> Get(CancellationToken cancellationToken)
    {
        return await database.Settings.AsNoTracking()
            .Select(settings => new SettingsResponse(settings.RetentionDays))
            .SingleAsync(cancellationToken);
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
        return new SettingsResponse(request.RetentionDays);
    }
}
