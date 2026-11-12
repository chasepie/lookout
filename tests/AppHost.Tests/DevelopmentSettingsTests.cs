using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.AppHost.Tests;

/// <summary>
/// How the development AppHost reads the repo root's <c>.env</c>.
/// </summary>
[TestClass]
public sealed class DevelopmentSettingsTests
{
    private const string Password = "a password long enough";

    private string _repoRoot = null!;

    [TestInitialize]
    public void CreateRepoRoot() =>
        _repoRoot = Directory.CreateTempSubdirectory("lookout-development-settings-").FullName;

    [TestCleanup]
    public void DeleteRepoRoot() => Directory.Delete(_repoRoot, recursive: true);

    [TestMethod]
    public void ReadsTheEnvFileInTheRepoRoot()
    {
        File.WriteAllText(Path.Join(_repoRoot, ".env"), "TZ=Europe/Berlin\n");

        var settings = DevelopmentSettings.Read(_repoRoot);

        Assert.AreEqual("Europe/Berlin", settings.TimeZone);
    }

    [TestMethod]
    public void ResolvesARelativeDirectoryAgainstTheRepoRootAndCreatesIt()
    {
        var settings = Settings(new() { [EnvVars.LookoutWatchScriptsDir] = "scripts" });

        var dir = settings.WatchScriptsDir();

        Assert.AreEqual(Path.GetFullPath(Path.Join(_repoRoot, "scripts")), dir);
        Assert.IsTrue(Directory.Exists(dir));
    }

    [TestMethod]
    public void KeepsAnAbsoluteDirectory()
    {
        var absolute = Path.Join(_repoRoot, "elsewhere", "database");
        var settings = Settings(new() { [EnvVars.LookoutDatabaseDir] = absolute });

        Assert.AreEqual(absolute, settings.DatabaseDir());
    }

    [TestMethod]
    [DataRow(EnvVars.LookoutWatchScriptsDir, "watch-scripts", null)]
    [DataRow(EnvVars.LookoutWatchScriptsDir, "watch-scripts", "")]
    [DataRow(EnvVars.LookoutDatabaseDir, "database", null)]
    [DataRow(EnvVars.LookoutDatabaseDir, "database", "   ")]
    [DataRow(EnvVars.GluetunDataDir, "gluetun-data", null)]
    public void PutsAnUnsetDirectoryInItsFolderInTheDataDirectoryAndCreatesIt(string name, string folder, string? value)
    {
        var settings = Settings(new() { [name] = value });

        var dir = name switch
        {
            EnvVars.LookoutWatchScriptsDir => settings.WatchScriptsDir(),
            EnvVars.LookoutDatabaseDir => settings.DatabaseDir(),
            _ => settings.GluetunDataDir(),
        };

        Assert.AreEqual(Path.GetFullPath(Path.Join(_repoRoot, DevelopmentSettings.DataDirectory, folder)), dir);
        Assert.IsTrue(Directory.Exists(dir));
    }

    [TestMethod]
    public void ReadsTheOwnersSignIn()
    {
        var settings = Settings(new() { [EnvVars.LookoutOwnerUsername] = "owner", [EnvVars.LookoutOwnerPassword] = Password });

        Assert.AreEqual(new OwnerCredentials("owner", Password), settings.RequireOwner());
    }

    [TestMethod]
    [DataRow(null, Password)]
    [DataRow("owner", null)]
    [DataRow("owner", " ")]
    public void RefusesToStartWithoutTheOwnersSignIn(string? username, string? password)
    {
        var settings = Settings(new() { [EnvVars.LookoutOwnerUsername] = username, [EnvVars.LookoutOwnerPassword] = password });

        var ex = Assert.Throws<InvalidOperationException>(settings.RequireOwner);

        Assert.Contains(EnvVars.LookoutOwnerUsername, ex.Message);
        Assert.Contains(EnvVars.LookoutOwnerPassword, ex.Message);
    }

    [TestMethod]
    public void RefusesAnOwnersPasswordThatIsTooShort()
    {
        var settings = Settings(new()
        {
            [EnvVars.LookoutOwnerUsername] = "owner",
            [EnvVars.LookoutOwnerPassword] = new string('p', EnvVars.OwnerPasswordMinLength - 1),
        });

        var ex = Assert.Throws<InvalidOperationException>(settings.RequireOwner);

        Assert.Contains($"at least {EnvVars.OwnerPasswordMinLength} characters", ex.Message);
    }

    [TestMethod]
    public void HasOpenVpnCredentialsOnlyWhenBothAreSet()
    {
        Assert.IsNull(Settings(new() { [SettingNames.GluetunOpenVpnUser] = "user" }).OpenVpn);
        Assert.IsNull(Settings(new() { [SettingNames.GluetunOpenVpnPassword] = "password" }).OpenVpn);
        Assert.AreEqual(
            new OpenVpnCredentials("user", "password"),
            Settings(new() { [SettingNames.GluetunOpenVpnUser] = "user", [SettingNames.GluetunOpenVpnPassword] = "password" }).OpenVpn);
    }

    [TestMethod]
    public void FallsBackToTheDefaultsForUnsetOptionalSettings()
    {
        var settings = Settings([]);

        Assert.AreEqual(SettingNames.DefaultTimeZone, settings.TimeZone);
        Assert.AreEqual(SettingNames.DefaultVpnServiceProvider, settings.VpnServiceProvider);
    }

    [TestMethod]
    public void ReadsTheMostRunsAtOnceOrItsDefault()
    {
        Assert.AreEqual(5, Settings(new() { [EnvVars.LookoutMaxConcurrentRuns] = "5" }).MaxConcurrentRuns);
        Assert.AreEqual(EnvVars.DefaultMaxConcurrentRuns, Settings([]).MaxConcurrentRuns);
    }

    [TestMethod]
    public void RefusesAMostRunsAtOnceBelowOne()
    {
        var settings = Settings(new() { [EnvVars.LookoutMaxConcurrentRuns] = "0" });

        var ex = Assert.Throws<InvalidOperationException>(() => settings.MaxConcurrentRuns);

        Assert.Contains(EnvVars.LookoutMaxConcurrentRuns, ex.Message);
    }

    private DevelopmentSettings Settings(Dictionary<string, string?> values) =>
        new(values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!), _repoRoot);
}
