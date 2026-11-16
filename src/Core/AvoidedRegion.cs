using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Core;

/// <summary>
/// A VPN region a watch's runs no longer run in, because one of its runs' known failures asked to avoid it. It stays
/// until the Owner clears it; a success in the region doesn't clear it. A watch avoids each region once.
/// </summary>
public class AvoidedRegion
{
    public int Id { get; set; }

    public required int WatchId { get; set; }

    [JsonIgnore]
    public Watch Watch { get; set; } = null!;

    /// <summary>
    /// The region, as gluetun's servers list spells it.
    /// </summary>
    public required string Region { get; set; }

    /// <summary>
    /// When the run asked to avoid it, in UTC.
    /// </summary>
    public required DateTime CreatedTime { get; set; }

    /// <summary>
    /// The run whose known failure asked to avoid it, or <see langword="null"/> once that run is deleted.
    /// </summary>
    public int? RunId { get; set; }

    [JsonIgnore]
    public WatchRun? Run { get; set; }
}
