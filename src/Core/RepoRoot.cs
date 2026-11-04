namespace ChasePie.Lookout.Core;

/// <summary>
/// Finds the repository root, the directory with <see cref="SolutionFileName"/>, which holds <c>.env</c>,
/// <c>examples</c> and the npm workspace's <c>node_modules</c>.
/// </summary>
public static class RepoRoot
{
    public const string SolutionFileName = "Lookout.slnx";

    /// <summary>
    /// Looks for the solution file in <paramref name="startDirectory"/> and its parents.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository, such as a project's directory.</param>
    /// <returns>The root's absolute path, or <see langword="null"/> if the directory isn't inside the repository.</returns>
    public static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <inheritdoc cref="Find"/>
    /// <returns>The root's absolute path.</returns>
    /// <exception cref="InvalidOperationException">The directory isn't inside the repository.</exception>
    public static string FindRequired(string startDirectory) =>
        Find(startDirectory) ?? throw new InvalidOperationException(
            $"Could not find the repository root: no {SolutionFileName} in {startDirectory} or its parents.");
}
