using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class OwnerCredentialsTests
{
    private const string Password = "correct horse battery";

    private static readonly OwnerCredentials Owner = new("owner", Password);

    private static IConfiguration Configuration(string? username, string? password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LookoutEnvironmentVariables.OwnerUsername] = username,
                [LookoutEnvironmentVariables.OwnerPassword] = password,
            })
            .Build();

    [TestMethod]
    [DataRow("owner", Password)]
    [DataRow("OWNER", Password)]
    [DataRow("Owner", Password)]
    public void MatchesTheCredentialsIgnoringTheUsernamesCase(string username, string password)
    {
        Assert.IsTrue(Owner.Matches(username, password));
    }

    [TestMethod]
    [DataRow("owner", "CORRECT HORSE BATTERY")]
    [DataRow("owner", "correct horse batter")]
    [DataRow("owner", "")]
    [DataRow("someone", Password)]
    [DataRow("", "")]
    public void RejectsAnythingElse(string username, string password)
    {
        Assert.IsFalse(Owner.Matches(username, password));
    }

    [TestMethod]
    public void TheFingerprintStaysTheSameForTheSameCredentials()
    {
        Assert.AreEqual(Owner.Fingerprint, new OwnerCredentials("owner", Password).Fingerprint);
    }

    [TestMethod]
    [DataRow("someone", Password)]
    [DataRow("owner", "a different password")]
    public void TheFingerprintChangesWithEitherCredential(string username, string password)
    {
        Assert.AreNotEqual(Owner.Fingerprint, new OwnerCredentials(username, password).Fingerprint);
    }

    [TestMethod]
    public void TheFingerprintDoesNotContainThePassword()
    {
        Assert.DoesNotContain(Password, Owner.Fingerprint);
    }

    [TestMethod]
    public void ReadsTheCredentialsFromConfiguration()
    {
        var owner = OwnerCredentials.FromConfiguration(Configuration("owner", Password));

        Assert.AreEqual("owner", owner.Username);
        Assert.IsTrue(owner.Matches("owner", Password));
    }

    [TestMethod]
    [DataRow(null, Password)]
    [DataRow(" ", Password)]
    [DataRow("owner", null)]
    [DataRow("owner", "")]
    public void MissingCredentialsStopStartup(string? username, string? password)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => OwnerCredentials.FromConfiguration(Configuration(username, password)));

        Assert.Contains(LookoutEnvironmentVariables.OwnerUsername, exception.Message);
    }

    [TestMethod]
    public void APasswordOneCharacterTooShortStopsStartup()
    {
        var password = new string('x', OwnerCredentials.MinPasswordLength - 1);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => OwnerCredentials.FromConfiguration(Configuration("owner", password)));

        Assert.Contains(LookoutEnvironmentVariables.OwnerPassword, exception.Message);
        Assert.Contains("16", exception.Message);
        Assert.DoesNotContain(password, exception.Message);
    }

    [TestMethod]
    public void APasswordOfTheMinimumLengthIsAccepted()
    {
        var password = new string('x', OwnerCredentials.MinPasswordLength);

        Assert.IsTrue(OwnerCredentials.FromConfiguration(Configuration("owner", password)).Matches("owner", password));
    }
}
