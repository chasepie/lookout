using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Model;

[JsonConverter(typeof(RunOutcomeJsonConverter))]
public enum RunOutcome
{
    Succeeded,
    KnownFailure,
    Errored
}

public sealed class RunOutcomeJsonConverter() : JsonStringEnumConverter<RunOutcome>(JsonNamingPolicy.CamelCase, false);

public sealed class Run
{
    public long Id { get; set; }
    public Guid WatchId { get; set; }
    public Guid ScriptVersionId { get; set; }
    public RunOutcome Outcome { get; set; }
    public string? Content { get; set; }
    public string? Details { get; set; }
    public string? ErrorMessage { get; set; }
    public double DurationMs { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool IsChange { get; set; }
    /// <summary>For a VPN Run, the region of the exit it went through; direct Runs have none.</summary>
    public string? VpnRegion { get; set; }
    /// <summary>For a VPN Run, the exit IP it went through; direct Runs have none.</summary>
    public string? VpnExitIp { get; set; }
}
