namespace ChasePie.Lookout.Model;

/// <summary>
/// The install-wide settings the user changes in the app, and what Lookout keeps about the install, in a single row.
/// </summary>
public sealed class Settings
{
    /// <summary>The only Id the settings can have, so the database holds one set of them.</summary>
    public const int SingletonId = 1;
    public const int DefaultRetentionDays = 30;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    public int Id { get; set; } = SingletonId;

    /// <summary>How many days a Run is kept before it is pruned, unless it is a Change.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// The IANA time zone daily and cron Schedules use. Startup sets it from configuration, or the host's time zone, when
    /// the install has none yet; once saved, it stays the install's even if configuration later names another.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>When Lookout last had the VPN refresh its list of servers, if it ever has.</summary>
    public DateTimeOffset? VpnServerListRefreshedAt { get; set; }
}
