using System.Net;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

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
            return Problem($"Keep Runs for between {Settings.MinRetentionDays} and {Settings.MaxRetentionDays} days.",
                statusCode: HttpStatusCode.BadRequest);
        }

        await database.Settings.ExecuteUpdateAsync(setters => setters
            .SetProperty(settings => settings.RetentionDays, request.RetentionDays), cancellationToken);
        return new SettingsResponse(request.RetentionDays);
    }
}

public sealed record SettingsResponse(int RetentionDays);

public sealed class SettingsRequest
{
    public required int RetentionDays { get; init; }
}
