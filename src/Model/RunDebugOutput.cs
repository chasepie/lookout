namespace ChasePie.Lookout.Model;

/// <summary>
/// What a Known failure or Errored Run left to debug it by. It is kept apart from its Run so that reading the Run
/// history doesn't load every screenshot, and only for each Watch's most recent Known failure and Errored Runs.
/// </summary>
public sealed class RunDebugOutput
{
    /// <summary>How many of a Watch's Known failure and Errored Runs keep their debug output: the most recent ones that left any.</summary>
    public const int KeptPerWatch = 20;

    public long RunId { get; set; }
    public Run? Run { get; set; }
    /// <summary>What the Script wrote to stdout and stderr.</summary>
    public string? ConsoleOutput { get; set; }
    /// <summary>A PNG of the Script's page, if the runner could take one.</summary>
    public byte[]? Screenshot { get; set; }
}
