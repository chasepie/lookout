namespace ChasePie.Lookout.Backend;

/// <summary>Checks the HTTP and HTTPS URLs Lookout is configured with or given by the user.</summary>
public static class HttpUrls
{
    /// <summary>The longest URL the user may give, such as a Notification URL or a Discord webhook URL.</summary>
    public const int MaxLength = 2048;

    /// <summary>Whether the user gave an absolute HTTP or HTTPS URL of at most <see cref="MaxLength"/> characters.</summary>
    public static bool IsValid(string url) =>
        url.Length <= MaxLength && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https";

    /// <summary>
    /// The base address configured as <paramref name="key"/>, or <paramref name="defaultUrl"/>, ending in a slash so
    /// relative request URIs keep its path. Throws if it isn't an absolute HTTP or HTTPS URL.
    /// </summary>
    public static Uri ConfiguredBaseAddress(IConfiguration configuration, string key, string defaultUrl)
    {
        var url = configuration[key] ?? defaultUrl;
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress) || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{key} must be an absolute HTTP or HTTPS URL, not '{url}'");
        }
        return baseAddress;
    }
}
