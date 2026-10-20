namespace ChasePie.Lookout.Backend;

/// <summary>
/// A second line of defence against cross-site scripting, as Angular's security guide recommends. Only Lookout's own
/// scripts run, DOM writes go through Angular's Trusted Types policies, and no other site can frame the app.
/// </summary>
public static class ContentSecurityPolicy
{
    private static readonly string Policy = string.Join("; ",
        "default-src 'self'",
        "script-src 'self'",
        // Angular adds component styles as <style> elements; allowing them needs a per-request nonce otherwise.
        // Nothing needs inline style="…" attributes, the likelier route for injected CSS, so those are blocked.
        // Browsers that predate these two directives fall back to style-src.
        "style-src 'self' 'unsafe-inline'",
        "style-src-elem 'self' 'unsafe-inline'",
        "style-src-attr 'none'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'",
        "require-trusted-types-for 'script'",
        // Angular's policy for its own DOM writes, and the bundler's for loading lazy chunks.
        "trusted-types angular angular#bundler");

    public static void Apply(WebApplication app)
    {
        var development = app.Environment.IsDevelopment();
        app.Use((context, next) =>
        {
            // The development-only Scalar API reference loads its scripts from a CDN.
            if (!(development && context.Request.Path.StartsWithSegments("/scalar")))
            {
                context.Response.Headers.ContentSecurityPolicy = Policy;
            }
            return next(context);
        });
    }
}
