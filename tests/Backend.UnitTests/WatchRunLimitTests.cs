using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class WatchRunLimitTests
{
    private static WatchRunLimit FromValue(string? value) =>
        WatchRunLimit.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [EnvVars.LookoutMaxConcurrentRuns] = value })
            .Build());

    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("8", 8)]
    [DataRow(" 5 ", 5)]
    public void ReadsTheLimit(string value, int expected)
    {
        Assert.AreEqual(expected, FromValue(value).MaxConcurrentRuns);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void FallsBackToTheDefaultWhenNotSet(string? value)
    {
        Assert.AreEqual(EnvVars.DefaultMaxConcurrentRuns, FromValue(value).MaxConcurrentRuns);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("two")]
    [DataRow("2.5")]
    [DataRow("99999999999")]
    public void RefusesAnythingButAWholeNumberOfAtLeastOne(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FromValue(value));

        Assert.Contains(EnvVars.LookoutMaxConcurrentRuns, ex.Message);
    }
}
