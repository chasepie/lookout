using ChasePie.Lookout.Backend.Database;
using System.Text;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Services;

public class WatchScriptManager(
    LookoutDbContext dbContext,
    WatchScriptsDirectory scriptsDirectory)
{
    private const int MaxScriptBytes = 64 * 1024;

    /// <summary>
    /// The largest request that uploads a script: the largest script, plus room for the rest of its multipart form.
    /// Kestrel stops reading a larger request at this size, rather than buffering all of it for the 64 KiB check.
    /// </summary>
    public const int MaxUploadRequestBytes = 2 * MaxScriptBytes;
    private const string InvalidScriptMessage = "Upload a .ts or .js script between 1 byte and 64 KiB.";

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
        // Checked before reading, so an oversized upload isn't read into memory
        if (scriptFile.Length > MaxScriptBytes)
        {
            return InvalidScriptMessage;
        }

        string source;
        using (var reader = new StreamReader(scriptFile.OpenReadStream()))
        {
            source = await reader.ReadToEndAsync(cancellationToken);
        }

        return await SaveScriptAsync(watch, scriptFile.FileName, source, cancellationToken);
    }

    /// <summary>
    /// Validates a script's source, saves it as the watch's script, and saves the watch.
    /// Works for a new watch that has been added to the context but not saved yet.
    /// </summary>
    /// <param name="fileName">The script's original file name, whose extension says how it runs.</param>
    /// <returns>Why the script was rejected, or <see langword="null"/> if it was saved.</returns>
    public async Task<string?> SaveScriptAsync(
        Watch watch, string fileName, string source, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".ts" or ".js") || Encoding.UTF8.GetByteCount(source) is < 1 or > MaxScriptBytes)
        {
            return InvalidScriptMessage;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return "Script source must not be empty.";
        }

        // Each upload gets a new file, so a failed save never touches the current script
        var previousFileName = WatchScriptsDirectory.GetStoredFileName(watch);
        watch.ScriptFileName = Path.GetFileName(fileName);
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
