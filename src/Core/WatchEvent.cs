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
    /// The first known failure in a known-failure streak to find the streak at or past the watch's threshold. A threshold
    /// lowered below a streak's length is reached by the streak's next known failure.
    /// </summary>
    KnownFailureStreak,

    /// <summary>
    /// A crash that starts a crash streak.
    /// </summary>
    Crash,

    /// <summary>
    /// The first success after a known-failure streak that showed a <see cref="KnownFailureStreak"/>, or after any
    /// crash streak, when it isn't also a <see cref="ContentChange"/>.
    /// </summary>
    Recovery,
}
