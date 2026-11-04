using System.ComponentModel.DataAnnotations;
using ChasePie.Lookout.Core;

namespace Backend.UnitTests;

[TestClass]
public sealed class VpnSettingsTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Regions = ["Albania", "Netherlands", "Switzerland"];

    private static VpnSettings Settings(int interval = 15, int jitter = 0) => new()
    {
        Id = VpnSettings.SingletonId,
        RotationEnabled = true,
        RotationIntervalMinutes = interval,
        RotationJitterMinutes = jitter,
    };

    private static VpnSettingsDto Dto(VpnSettings from) => new()
    {
        RotationEnabled = from.RotationEnabled,
        RotationIntervalMinutes = from.RotationIntervalMinutes,
        RotationJitterMinutes = from.RotationJitterMinutes,
        ExcludedRegions = [.. from.ExcludedRegions],
    };

    [TestMethod]
    [DataRow(0.0, 12.0)]
    [DataRow(0.5, 15.0)]
    [DataRow(0.75, 16.5)]
    public void RotationDelayIsTheIntervalMovedByUpToTheJitter(double sample, double expectedMinutes)
    {
        var delay = Settings(interval: 15, jitter: 3).GetRotationDelay(sample);

        Assert.AreEqual(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [TestMethod]
    public void ScheduledRotationStaysWithinTheJitter()
    {
        var settings = Settings(interval: 15, jitter: 3);
        var random = new Random(42);

        for (var i = 0; i < 100; i++)
        {
            settings.ScheduleNextRotation(Now, random);

            Assert.IsNotNull(settings.NextRotationTime);
            Assert.IsTrue(settings.NextRotationTime >= Now.AddMinutes(12));
            Assert.IsTrue(settings.NextRotationTime < Now.AddMinutes(18));
        }
    }

    [TestMethod]
    public void NoRotationIsScheduledWhileRotationIsOff()
    {
        var settings = Settings();
        settings.RotationEnabled = false;

        settings.ScheduleNextRotation(Now, new Random(1));

        Assert.IsNull(settings.NextRotationTime);
    }

    [TestMethod]
    public void RotationNeverPicksTheCurrentOrAnExcludedRegion()
    {
        var settings = Settings();
        settings.CurrentRegion = "netherlands";
        settings.ExcludedRegions = ["ALBANIA"];
        var random = new Random(7);

        for (var i = 0; i < 20; i++)
        {
            Assert.AreEqual("Switzerland", settings.PickRotationRegion(Regions, random));
        }
    }

    [TestMethod]
    public void RotationPicksNothingWhenOnlyTheCurrentRegionIsAllowed()
    {
        var settings = Settings();
        settings.CurrentRegion = "Netherlands";
        settings.ExcludedRegions = ["Albania", "Switzerland"];

        Assert.IsNull(settings.PickRotationRegion(Regions, new Random(1)));
    }

    [TestMethod]
    public void ChangingTheIntervalCountsFromTheLastSwitch()
    {
        var settings = Settings(interval: 15);
        settings.LastSwitchTime = Now.AddMinutes(-10);
        var dto = Dto(settings);
        dto.RotationIntervalMinutes = 30;

        settings.Update(dto, Now, new Random(1));

        Assert.AreEqual(Now.AddMinutes(20), settings.NextRotationTime);
    }

    [TestMethod]
    public void ShorteningTheIntervalPastTheLastSwitchRotatesNow()
    {
        var settings = Settings(interval: 30);
        settings.LastSwitchTime = Now.AddMinutes(-20);
        var dto = Dto(settings);
        dto.RotationIntervalMinutes = 5;

        settings.Update(dto, Now, new Random(1));

        Assert.AreEqual(Now, settings.NextRotationTime);
    }

    [TestMethod]
    public void ChangingOnlyExclusionsKeepsTheScheduledRotation()
    {
        var settings = Settings();
        settings.NextRotationTime = Now.AddMinutes(7);
        var dto = Dto(settings);
        dto.ExcludedRegions = ["Albania", "albania", " ", "Netherlands"];

        settings.Update(dto, Now, new Random(1));

        Assert.AreEqual(Now.AddMinutes(7), settings.NextRotationTime);
        Assert.AreSequenceEqual(["Albania", "Netherlands"], settings.ExcludedRegions);
    }

    [TestMethod]
    public void TurningRotationOffClearsTheScheduledRotation()
    {
        var settings = Settings();
        settings.NextRotationTime = Now.AddMinutes(7);
        var dto = Dto(settings);
        dto.RotationEnabled = false;

        settings.Update(dto, Now, new Random(1));

        Assert.IsNull(settings.NextRotationTime);
    }

    [TestMethod]
    [DataRow(15, 14, true)]
    [DataRow(15, 15, false)]
    [DataRow(5, 0, true)]
    public void JitterMustBeLessThanTheInterval(int interval, int jitter, bool expectedValid)
    {
        var dto = new VpnSettingsDto { RotationIntervalMinutes = interval, RotationJitterMinutes = jitter };

        var valid = Validator.TryValidateObject(dto, new ValidationContext(dto), [], validateAllProperties: true);

        Assert.AreEqual(expectedValid, valid);
    }
}
