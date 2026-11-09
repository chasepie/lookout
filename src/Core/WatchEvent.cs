namespace ChasePie.Lookout.Core;

/// <summary>
/// Something a run showed that the Owner can be notified about. A run shows at most one.
/// </summary>
public enum WatchEvent
{
    /// <summary>
    /// A success whose content differs from the content of the watch's previous success.
    /// </summary>
    ContentChange,

    /// <summary>
    /// A known failure that brings the watch's known-failure streak up to the watch's threshold.
    /// </summary>
    KnownFailureStreak,

    /// <summary>
    /// A crash that starts a crash streak.
    /// </summary>
    Crash,

    /// <summary>
    /// The first success after a known-failure streak that reached the watch's threshold, or after any crash streak,
    /// when it isn't also a <see cref="ContentChange"/>.
    /// </summary>
    Recovery,
}
