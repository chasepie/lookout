using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// The <c>LOOKOUT_WATCH_SCRIPTS_DIR</c> directory, where the Backend saves uploaded scripts and the Runner
/// runs them from. Resolve it once at startup with <see cref="FromConfiguration"/>.
/// </summary>
/// <param name="FullPath">The directory's absolute path.</param>
public sealed record WatchScriptsDirectory(string FullPath)
{
    /// <summary>
    /// Reads the directory from configuration and creates it if it doesn't exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>LOOKOUT_WATCH_SCRIPTS_DIR</c> is not set.</exception>
    public static WatchScriptsDirectory FromConfiguration(IConfiguration configuration)
    {
        var configuredDir = configuration[LookoutEnvironmentVariables.WatchScriptsDir];
        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            throw new InvalidOperationException(
                $"Environment variable {LookoutEnvironmentVariables.WatchScriptsDir} must be set");
        }

        // Whichever service starts first creates it
        var fullPath = Path.GetFullPath(configuredDir);
        Directory.CreateDirectory(fullPath);
        return new WatchScriptsDirectory(fullPath);
    }

    /// <summary>
    /// The name of the watch's script file in the directory, or <see langword="null"/> if it has no script.
    /// </summary>
    public static string? GetStoredFileName(Watch watch)
    {
        if (watch.ScriptFileId is { } fileId)
        {
            return fileId + Path.GetExtension(watch.ScriptFileName);
        }

        return null;
    }
}
