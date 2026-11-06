namespace ChasePie.Lookout.Runner.Services;

/// <summary>
/// Resolves paths through symbolic links. Node's permission model only matches a path whose every part is real, so a
/// grant for <c>/var/scripts</c> doesn't cover a script it loads from there when <c>/var</c> is a link, as on macOS.
/// </summary>
internal static class RealPath
{
    /// <summary>
    /// <paramref name="path"/> made absolute, with every link in it resolved. Parts that don't exist yet, such as a file
    /// about to be written, are kept as they are.
    /// </summary>
    public static string Of(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        var parts = fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var next = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            // A link's target can itself run through links, as /var/... does on macOS
            current = (info.Exists || info.LinkTarget is not null) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Of(target.FullName)
                : next;
        }

        return current;
    }
}
