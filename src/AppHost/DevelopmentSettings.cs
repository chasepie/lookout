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
    public static DevelopmentSettings Read(string repoRoot) =>
        new(DotEnv.Read(new DotEnvOptions(envFilePaths: [Path.Combine(repoRoot, ".env")])).AsReadOnly(), repoRoot);

    public string RepoRoot => repoRoot;

    public string TimeZone => Value(SettingNames.TimeZone) ?? "America/New_York";

    public string VpnServiceProvider => Value(SettingNames.GluetunVpnServiceProvider) ?? SettingNames.DefaultVpnServiceProvider;

    /// <summary>
    /// Where the Backend keeps its database, or <see langword="null"/> for the Backend's own default.
    /// </summary>
    public string? DatabaseDir => Directory(EnvVars.LookoutDatabaseDir);

    /// <summary>
    /// The VPN provider's OpenVPN credentials, or <see langword="null"/> unless both are set. Without them gluetun isn't
    /// started, and runs of watches that use the VPN fail.
    /// </summary>
    public OpenVpnCredentials? OpenVpn =>
        Value(SettingNames.GluetunOpenVpnUser) is { } user && Value(SettingNames.GluetunOpenVpnPassword) is { } password
            ? new OpenVpnCredentials(user, password)
            : null;

    /// <summary>
    /// The directory the Backend saves uploaded scripts to and the Runner runs them from, created if it doesn't exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">It isn't set.</exception>
    public string RequireWatchScriptsDir() => RequireDirectory(EnvVars.LookoutWatchScriptsDir, "watch scripts are kept in");

    /// <summary>
    /// gluetun's data directory, created if it doesn't exist yet. gluetun keeps its servers list there, and the Backend
    /// reads the list to know which regions exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">It isn't set.</exception>
    public string RequireGluetunDataDir() => RequireDirectory(EnvVars.GluetunDataDir, "gluetun keeps its data in");

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

    private string? Directory(string name) => Value(name) is { } dir ? Path.GetFullPath(dir, repoRoot) : null;

    private string RequireDirectory(string name, string purpose)
    {
        var dir = Directory(name) ?? throw new InvalidOperationException($"Set {name} in .env to the directory {purpose}.");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }
}

internal sealed record OwnerCredentials(string Username, string Password);

internal sealed record OpenVpnCredentials(string User, string Password);
