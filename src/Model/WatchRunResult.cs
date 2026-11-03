namespace ChasePie.Lookout.Runner.Services;

/// <summary>
/// The JSON result printed by <c>lookout-watch run</c>; mirrors the SDK's <c>RunResult</c> union.
/// </summary>
/// <param name="Success">Whether the watch script succeeded.</param>
/// <param name="Content">The script's output when <paramref name="Success"/> is <see langword="true"/>.</param>
/// <param name="FailureReason">Why the script failed when <paramref name="Success"/> is <see langword="false"/>.</param>
public sealed record WatchRunResult(bool Success, string? Content, string? FailureReason);
