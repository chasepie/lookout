using System.Globalization;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// What a run's screenshot must be. The Runner doesn't check it: the script runs in the same process as the watch CLI,
/// so what the CLI sends could be anything.
/// </summary>
public static class ScreenshotImage
{
    /// <summary>
    /// The largest screenshot a run keeps, 8 MiB, as the watch SDK's <c>maxScreenshotBytes</c> is.
    /// </summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static ReadOnlySpan<byte> JpegSignature => [0xff, 0xd8, 0xff];

    /// <summary>
    /// The image's media type, from its first bytes: <c>image/png</c> or <c>image/jpeg</c>, or <see langword="null"/>
    /// if it's neither.
    /// </summary>
    public static string? MediaTypeOf(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) ? "image/png"
        : bytes.StartsWith(JpegSignature) ? "image/jpeg"
        : null;

    /// <summary>
    /// Why the script's screenshot can't be kept, or <see langword="null"/> if it can.
    /// </summary>
    public static string? ProblemWith(ReadOnlySpan<byte> bytes)
    {
        if (MediaTypeOf(bytes) is null)
        {
            return "The script's screenshot isn't a PNG or JPEG image.";
        }

        return bytes.Length > MaxBytes
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The script's screenshot is {bytes.Length / 1024.0 / 1024.0:0.0} MiB, over the 8 MiB a run can keep.")
            : null;
    }
}
