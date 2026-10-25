using System.Net;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record ScriptVersionSummary(
    Guid Id,
    int Number,
    string FileKind,
    DateTimeOffset UploadedAt,
    bool IsCurrent);

public sealed record ScriptVersionSource(
    Guid Id,
    int Number,
    string FileKind,
    DateTimeOffset UploadedAt,
    bool IsCurrent,
    string Source);

public sealed class UploadScriptRequest
{
    public required IFormFile Script { get; init; }

    /// <summary>
    /// Keeps the Baseline and failure streak, for a new version that reports its content the same way.
    /// </summary>
    public bool KeepBaseline { get; init; }
}

/// <summary>A Watch's Script versions: uploading a new one, which becomes current, and reading past ones.</summary>
[ApiController]
[Route("api/watches")]
public sealed class ScriptVersionsController(
    LookoutDbContext database,
    WatchEvents watchEvents,
    TimeProvider timeProvider)
    : LookoutControllerBase
{
    [HttpPost("{id:guid}/script-versions")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<ScriptVersionSummary>> UploadScript(
        Guid id,
        [FromForm] UploadScriptRequest request,
        CancellationToken cancellationToken)
    {
        var (script, scriptProblem) = await UploadedScript.ReadAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return Problem(scriptProblem!, statusCode: HttpStatusCode.BadRequest);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (await WatchStatuses.ReadAsync(database.Watches, id, cancellationToken) is not { } previousStatus)
        {
            return NotFound();
        }

        // The transaction holds SQLite's write lock from the start, so no other upload can take the same number.
        var latestNumber = await database.ScriptVersions
            .Where(version => version.WatchId == id)
            .MaxAsync(version => (int?)version.Number, cancellationToken);
        var version = new ScriptVersion
        {
            WatchId = id,
            Number = (latestNumber ?? 0) + 1,
            UploadedAt = timeProvider.GetUtcNow(),
            Source = script.Source,
            FileKind = script.FileKind
        };
        database.ScriptVersions.Add(version);
        await database.SaveChangesAsync(cancellationToken);

        // Advancing the Revision discards a Run of the replaced version still in progress, which would otherwise set
        // the Baseline from the old Script's content. A set-based update leaves the Watch's other settings alone.
        await database.Watches
            .Where(watch => watch.Id == id)
            .ExecuteUpdateAsync(setters =>
            {
                setters
                    .SetProperty(watch => watch.CurrentScriptVersionId, version.Id)
                    .SetProperty(watch => watch.Revision, watch => watch.Revision + 1);
                // A new Script may report its content differently, so by default it starts afresh, as Pending.
                // Its failures are a new streak, which notifies afresh.
                if (!request.KeepBaseline)
                {
                    setters
                        .SetProperty(watch => watch.Baseline, (string?)null)
                        .SetProperty(watch => watch.FailureStreak, 0)
                        .SetProperty(watch => watch.FailureStreakNotified, false);
                }
            }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (await WatchStatuses.ReadAsync(database.Watches, id, CancellationToken.None) is { } status && status != previousStatus)
        {
            await watchEvents.StatusChangedAsync(id, status);
        }

        var summary = new ScriptVersionSummary(
            Id: version.Id,
            Number: version.Number,
            FileKind: version.FileKind,
            UploadedAt: version.UploadedAt,
            IsCurrent: true);
        return StatusCode(StatusCodes.Status201Created, summary);
    }

    [HttpGet("{id:guid}/script-versions")]
    public async Task<ActionResult<IReadOnlyList<ScriptVersionSummary>>> ListScriptVersions(Guid id, CancellationToken cancellationToken)
    {
        // One query, so an upload committing meanwhile can't leave the list and its current version out of step.
        var versions = await database.ScriptVersions.AsNoTracking()
            .Where(version => version.WatchId == id)
            .OrderByDescending(version => version.Number)
            .Select(version => new ScriptVersionSummary(
                Id: version.Id,
                Number: version.Number,
                FileKind: version.FileKind,
                UploadedAt: version.UploadedAt,
                IsCurrent: database.Watches.Any(watch =>
                    watch.Id == id
                    && watch.CurrentScriptVersionId == version.Id)))
            .ToListAsync(cancellationToken);

        // Every Watch has at least the Script version it was created with.
        return versions.Count == 0 ? NotFound() : versions;
    }

    [HttpGet("{id:guid}/script-versions/{versionId:guid}")]
    public async Task<ActionResult<ScriptVersionSource>> GetScriptVersion(Guid id, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await database.ScriptVersions.AsNoTracking()
            .Where(version => version.Id == versionId && version.WatchId == id)
            .Select(version => new ScriptVersionSource(
                Id: version.Id,
                Number: version.Number,
                FileKind: version.FileKind,
                UploadedAt: version.UploadedAt,
                IsCurrent: database.Watches.Any(watch =>
                    watch.Id == id
                    && watch.CurrentScriptVersionId == version.Id),
                Source: version.Source))
            .SingleOrDefaultAsync(cancellationToken);
        return version is null ? NotFound() : version;
    }
}
