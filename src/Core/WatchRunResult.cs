namespace ChasePie.Lookout.Core;

/// <summary>
/// How a watch script ended, as <c>lookout-watch run</c> writes it: the SDK's <c>RunResult</c>, a success or a known
/// failure, or a crash in its place.
/// </summary>
/// <param name="Success">Whether the script returned a success.</param>
/// <param name="Content">The script's content when <paramref name="Success"/> is <see langword="true"/>.</param>
/// <param name="FailureReason">The known failure's reason, when the script returned one.</param>
/// <param name="Screenshot">
/// The script's own screenshot, or one the CLI took; base64 in JSON. Not yet checked to be an image.
/// </param>
/// <param name="Crash">What the script threw, when it crashed instead of returning a result.</param>
/// <param name="Failover">
/// Whether the known failure asked for a failover: a switch of the VPN to another region, and a retry there.
/// </param>
public sealed record WatchRunResult(
    bool Success,
    string? Content,
    string? FailureReason,
    byte[]? Screenshot = null,
    WatchRunCrash? Crash = null,
    bool Failover = false);

/// <summary>
/// A watch script that threw, or returned something other than a result.
/// </summary>
/// <param name="Error">What it threw: its stack, which starts with its message, if it's an error.</param>
public sealed record WatchRunCrash(string Error);
