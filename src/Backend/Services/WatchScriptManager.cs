using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Services;

public class WatchScriptManager(
    LookoutDbContext dbContext,
    WatchScriptsDirectory scriptsDirectory)
{
    private const long MaxScriptBytes = 64 * 1024;

    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly WatchScriptsDirectory _scriptsDirectory = scriptsDirectory;

    /// <summary>
    /// Validates an uploaded script, saves it as the watch's script, and saves the watch.
    /// Works for a new watch that has been added to the context but not saved yet.
    /// </summary>
    /// <returns>Why the script was rejected, or <see langword="null"/> if it was saved.</returns>
    public async Task<string?> SaveScriptAsync(
        Watch watch, IFormFile scriptFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(scriptFile.FileName).ToLowerInvariant();
        if (extension is not (".ts" or ".js") || scriptFile.Length is < 1 or > MaxScriptBytes)
        {
            return "Upload a .ts or .js script between 1 byte and 64 KiB.";
        }

        string source;
        using (var reader = new StreamReader(scriptFile.OpenReadStream()))
        {
            source = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return "Script source must not be empty.";
        }

        // Each upload gets a new file, so a failed save never touches the current script
        var previousFileName = WatchScriptsDirectory.GetStoredFileName(watch);
        watch.ScriptFileName = Path.GetFileName(scriptFile.FileName);
        watch.ScriptFileId = Guid.NewGuid();
        var savePath = GetPath(WatchScriptsDirectory.GetStoredFileName(watch)!);

        try
        {
            await File.WriteAllTextAsync(savePath, source, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            File.Delete(savePath);
            throw;
        }

        if (previousFileName is not null)
        {
            File.Delete(GetPath(previousFileName));
        }

        return null;
    }

    /// <summary>
    /// Deletes the watch's script file, if it has one. Call it after the watch's deletion is saved, so a failed
    /// save leaves the watch with its script.
    /// </summary>
    public void DeleteScript(Watch watch)
    {
        if (WatchScriptsDirectory.GetStoredFileName(watch) is { } storedFileName)
        {
            File.Delete(GetPath(storedFileName));
        }
    }

    private string GetPath(string storedFileName) => Path.Combine(_scriptsDirectory.FullPath, storedFileName);
}
