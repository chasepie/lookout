using ChasePie.Lookout.Core;
using dotenv.net;

namespace ChasePie.Lookout.AppHost;

/// <summary>
/// The settings in the repo root's <c>.env</c>, which the development AppHost forwards to the services: Aspire starts each
/// project from its own directory, where the services can't find <c>.env</c> themselves. A value that's empty or only
/// whitespace counts as not set, and a relative directory is resolved against the repo root, where <c>.env</c> lives.
/// </summary>
internal sealed class DevelopmentSettings(IReadOnlyDictionary<string, string> values, string repoRoot)
{
    /// <summary>
    /// Where the services keep their data unless <c>.env</c> says otherwise, under the repo root and ignored by git: a
    /// folder each, which the containers bind-mount.
    /// </summary>
    public const string DataDirectory = ".docker-volumes";

    public static DevelopmentSettings Read(string repoRoot)
    {
        var options = new DotEnvOptions(envFilePaths: [Path.Combine(repoRoot, ".env")]);
        return new(DotEnv.Read(options).AsReadOnly(), repoRoot);
    }

    public string RepoRoot => repoRoot;

    public string TimeZone => Value(SettingNames.TimeZone) ?? SettingNames.DefaultTimeZone;

    public string VpnServiceProvider => Value(SettingNames.GluetunVpnServiceProvider) ?? SettingNames.DefaultVpnServiceProvider;

    /// <summary>
    /// How many runs the Backend runs at once at most, which browserless takes as many sessions at once for. See
    /// <see cref="EnvVars.LookoutMaxConcurrentRuns"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">It isn't a whole number of at least 1.</exception>
    public int MaxConcurrentRuns => EnvVars.ParseMaxConcurrentRuns(Value(EnvVars.LookoutMaxConcurrentRuns));

    /// <summary>
    /// The VPN regions Lookout may use, comma-separated, or <see langword="null"/> for every region. See
    /// <see cref="EnvVars.GluetunServerRegions"/>.
    /// </summary>
    public string? GluetunServerRegions => Value(EnvVars.GluetunServerRegions);

    /// <summary>
    /// The VPN provider's OpenVPN credentials, or <see langword="null"/> unless both are set. Without them gluetun isn't
    /// started, and runs of watches that use the VPN fail.
    /// </summary>
    public OpenVpnCredentials? OpenVpn =>
        Value(SettingNames.GluetunOpenVpnUser) is { } user && Value(SettingNames.GluetunOpenVpnPassword) is { } password
            ? new OpenVpnCredentials(user, password)
            : null;

    /// <summary>
    /// The directory the Backend saves uploaded scripts to and the Runner runs them from, created if it doesn't exist yet:
    /// <c>.docker-volumes/watch-scripts</c> unless <c>.env</c> says otherwise.
    /// </summary>
    public string WatchScriptsDir() => DirectoryOrDefault(EnvVars.LookoutWatchScriptsDir, "watch-scripts");

    /// <summary>
    /// The directory the Backend keeps its database and Data Protection keys in, created if it doesn't exist yet:
    /// <c>.docker-volumes/database</c> unless <c>.env</c> says otherwise.
    /// </summary>
    public string DatabaseDir() => DirectoryOrDefault(EnvVars.LookoutDatabaseDir, "database");

    /// <summary>
    /// gluetun's data directory, created if it doesn't exist yet: <c>.docker-volumes/gluetun-data</c> unless <c>.env</c>
    /// says otherwise. gluetun keeps its servers list there, and the Backend reads the list to know which regions exist.
    /// </summary>
    public string GluetunDataDir() => DirectoryOrDefault(EnvVars.GluetunDataDir, "gluetun-data");

    /// <summary>
    /// The Owner's sign-in. The Backend checks it too; checking here stops startup before anything else starts.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Either isn't set, or the password is shorter than <see cref="EnvVars.OwnerPasswordMinLength"/>.
    /// </exception>
    public OwnerCredentials RequireOwner()
    {
        if (Value(EnvVars.LookoutOwnerUsername) is not { } username
            || Value(EnvVars.LookoutOwnerPassword) is not { } password)
        {
            throw new InvalidOperationException(
                $"Set {EnvVars.LookoutOwnerUsername} and {EnvVars.LookoutOwnerPassword} "
                + "in .env to the username and password you sign in to Lookout with.");
        }

        if (password.Length < EnvVars.OwnerPasswordMinLength)
        {
            throw new InvalidOperationException(
                $"{EnvVars.LookoutOwnerPassword} in .env must be at least {EnvVars.OwnerPasswordMinLength} characters.");
        }

        return new OwnerCredentials(username, password);
    }

    private string? Value(string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private string DirectoryOrDefault(string name, string defaultFolder)
    {
        var dir = Path.GetFullPath(Value(name) ?? Path.Join(DataDirectory, defaultFolder), repoRoot);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal sealed record OwnerCredentials(string Username, string Password);

internal sealed record OpenVpnCredentials(string User, string Password);
