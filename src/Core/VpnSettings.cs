using System.ComponentModel.DataAnnotations;

namespace ChasePie.Lookout.Core;

/// <summary>
/// How the VPN rotates between regions. Edited on the frontend's VPN page.
/// </summary>
public class VpnSettingsDto : IValidatableObject
{
    /// <summary>
    /// Whether the VPN switches to another region every <see cref="RotationIntervalMinutes"/>.
    /// </summary>
    public bool RotationEnabled { get; set; }

    [Range(1, 7 * 24 * 60)]
    public int RotationIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// How far each rotation may land before or after the interval, picked at random each time,
    /// so the switches don't follow a regular rhythm. 0 rotates exactly on the interval.
    /// </summary>
    [Range(0, 7 * 24 * 60)]
    public int RotationJitterMinutes { get; set; } = 3;

    /// <summary>
    /// Regions rotation never picks. A manual switch can still go to them.
    /// </summary>
    public List<string> ExcludedRegions { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RotationJitterMinutes >= RotationIntervalMinutes)
        {
            yield return new ValidationResult(
                $"{nameof(RotationJitterMinutes)} must be less than {nameof(RotationIntervalMinutes)}.",
                [nameof(RotationJitterMinutes)]);
        }
    }
}

/// <summary>
/// The VPN settings and the rotation's state. There's a single row, with <see cref="SingletonId"/>.
/// </summary>
public class VpnSettings : VpnSettingsDto
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    /// <summary>
    /// The region the Backend last switched the VPN to, which it reapplies if gluetun restarts and forgets it.
    /// <see langword="null"/> until the first switch; until then gluetun picks any region.
    /// </summary>
    public string? CurrentRegion { get; set; }

    /// <summary>
    /// When the VPN last switched region, in UTC.
    /// </summary>
    public DateTime? LastSwitchTime { get; set; }

    /// <summary>
    /// When rotation next switches region, in UTC, or <see langword="null"/> while rotation is off.
    /// Stored rather than computed so the random jitter is picked once per rotation.
    /// </summary>
    public DateTime? NextRotationTime { get; set; }

    /// <summary>
    /// Why the last switch failed, cleared by the next one that succeeds.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Applies edited settings, rescheduling the next rotation if its timing changed.
    /// </summary>
    /// <param name="now">The current time, in UTC.</param>
    public void Update(VpnSettingsDto dto, DateTime now, Random random)
    {
        var timingChanged = dto.RotationEnabled != RotationEnabled
            || dto.RotationIntervalMinutes != RotationIntervalMinutes
            || dto.RotationJitterMinutes != RotationJitterMinutes;

        RotationEnabled = dto.RotationEnabled;
        RotationIntervalMinutes = dto.RotationIntervalMinutes;
        RotationJitterMinutes = dto.RotationJitterMinutes;
        ExcludedRegions =
        [
            .. dto.ExcludedRegions
                .Where(region => !string.IsNullOrWhiteSpace(region))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        if (timingChanged)
        {
            // Count from the last switch, so lengthening the interval doesn't restart the wait,
            // but never schedule in the past: a shortened interval rotates now rather than "already late"
            ScheduleNextRotation(LastSwitchTime ?? now, random);
            if (NextRotationTime < now)
            {
                NextRotationTime = now;
            }
        }
    }

    /// <summary>
    /// Sets <see cref="NextRotationTime"/> to one jittered interval after <paramref name="from"/>,
    /// or clears it if rotation is off.
    /// </summary>
    public void ScheduleNextRotation(DateTime from, Random random)
    {
        NextRotationTime = RotationEnabled ? from + GetRotationDelay(random.NextDouble()) : null;
    }

    /// <summary>
    /// The time between two rotations: the interval, moved by up to the jitter either way.
    /// </summary>
    /// <param name="sample">A random number in [0, 1); 0.5 gives exactly the interval.</param>
    public TimeSpan GetRotationDelay(double sample)
    {
        var offsetMinutes = (2 * sample - 1) * RotationJitterMinutes;
        return TimeSpan.FromMinutes(RotationIntervalMinutes + offsetMinutes);
    }

    /// <summary>
    /// Picks a random region that isn't excluded and isn't <see cref="CurrentRegion"/>. Staying in the current
    /// region would leave gluetun on the same server, since it skips settings that didn't change.
    /// </summary>
    /// <param name="regions">The regions the VPN provider offers.</param>
    /// <returns>The region, or <see langword="null"/> if no other region is allowed.</returns>
    public string? PickRotationRegion(IReadOnlyCollection<string> regions, Random random) =>
        PickFailoverRegion(regions, [], random);

    /// <summary>
    /// Picks a region as <see cref="PickRotationRegion"/> does, but not one of <paramref name="failedRegions"/>.
    /// </summary>
    /// <param name="regions">The regions the VPN provider offers.</param>
    /// <param name="failedRegions">
    /// The regions the watch asked to fail over from during its current known-failure streak.
    /// </param>
    /// <returns>The region, or <see langword="null"/> if no region is left.</returns>
    public string? PickFailoverRegion(
        IReadOnlyCollection<string> regions, IReadOnlyCollection<string> failedRegions, Random random)
    {
        var candidates = regions
            .Where(region => !ExcludedRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
            .Where(region => !string.Equals(region, CurrentRegion, StringComparison.OrdinalIgnoreCase))
            .Where(region => !failedRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
    }
}
