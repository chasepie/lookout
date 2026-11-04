using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Database;

/// <summary>
/// Gives a fresh development database a watch for each example script, so there's something to run.
/// </summary>
public partial class DevelopmentDataSeeder(
    LookoutDbContext dbContext,
    WatchScriptManager scriptManager,
    ILogger<DevelopmentDataSeeder> logger)
{
    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly WatchScriptManager _scriptManager = scriptManager;

    /// <summary>
    /// The repository's <c>examples</c> directory; see <see cref="RepoRoot.Find"/>.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository, such as the Backend's content root.</param>
    /// <returns>
    /// The directory's path, or <see langword="null"/> if the start directory isn't inside the repository or the
    /// repository has no examples.
    /// </returns>
    public static string? FindExamplesDirectory(string startDirectory)
    {
        if (RepoRoot.Find(startDirectory) is not { } repoRoot)
        {
            return null;
        }

        var examplesDir = Path.Combine(repoRoot, "examples");
        return Directory.Exists(examplesDir) ? examplesDir : null;
    }

    /// <summary>
    /// Creates an unscheduled watch, without the VPN, for each <c>.ts</c> script in <paramref name="examplesDir"/>,
    /// named after its file. Does nothing if the directory doesn't exist or the database already has watches,
    /// so deleted watches stay deleted.
    /// </summary>
    public async Task SeedAsync(string examplesDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(examplesDir) || await _dbContext.Watches.AnyAsync(cancellationToken))
        {
            return;
        }

        var scriptPaths = Directory.EnumerateFiles(examplesDir, "*.ts").Order(StringComparer.Ordinal);
        foreach (var scriptPath in scriptPaths)
        {
            var fileName = Path.GetFileName(scriptPath);
            var source = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            var watch = new Watch { Name = Path.GetFileNameWithoutExtension(fileName) };
            _dbContext.Watches.Add(watch);

            // Saves the watch and its script together
            var errorMessage = await _scriptManager.SaveScriptAsync(watch, fileName, source, cancellationToken);
            if (errorMessage is not null)
            {
                // Detaches the unsaved watch, so the next script's save doesn't save it without a script
                _dbContext.Watches.Remove(watch);
                LogExampleSkipped(fileName, errorMessage);
                continue;
            }

            LogExampleSeeded(fileName, watch.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped example script {FileName}: {Reason}")]
    private partial void LogExampleSkipped(string fileName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created watch {WatchId} for example script {FileName}")]
    private partial void LogExampleSeeded(string fileName, int watchId);
}
