namespace ChasePie.Lookout.Backend.Hosting;

/// <summary>
/// The hostnames Lookout answers to: <c>AllowedHosts</c>, which ASP.NET Core's host filtering reads. <c>appsettings.json</c>
/// sets <c>localhost</c>, and a deployment its own hostname. See docs/security.md.
/// </summary>
public static class AllowedHosts
{
    /// <summary>
    /// Stops startup if <c>AllowedHosts</c> is empty, which host filtering would take as every host: a deployment that
    /// left its hostname unset would otherwise answer to any name without anyone noticing.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>AllowedHosts</c> is empty.</exception>
    public static void Require(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["AllowedHosts"]))
        {
            throw new InvalidOperationException(
                "Set AllowedHosts to the hostname Lookout is served on, such as lookout.example.com.");
        }
    }
}
