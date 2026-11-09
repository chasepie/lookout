namespace ChasePie.Lookout.Core;

/// <summary>
/// For each way a run can end, whether Lookout takes a full-page screenshot of the page given to the watch script,
/// when the script returned none of its own.
/// </summary>
/// <param name="OnSuccess">When the script returns a success.</param>
/// <param name="OnKnownFailure">When the script returns a known failure.</param>
/// <param name="OnCrash">When the script throws. A run that times out or is cancelled gets none.</param>
public sealed record ScreenshotSettings(bool OnSuccess = false, bool OnKnownFailure = false, bool OnCrash = false)
{
    public static readonly ScreenshotSettings None = new();
}
