namespace ChasePie.Lookout.Backend.Hosting;

/// <summary>
/// The headers that tell browsers how to treat Lookout's responses. See docs/security.md.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Only Lookout's own files, so injected markup can't load or send anything elsewhere. Angular adds component styles
    /// as <c>&lt;style&gt;</c> elements, hence <c>'unsafe-inline'</c> for styles but not scripts, and screenshots are
    /// shown as <c>data:</c> URLs. <c>frame-ancestors 'none'</c> stops other sites framing Lookout to trick clicks.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; "
        + "frame-ancestors 'none'";

    /// <summary>
    /// The Scalar API reference, in development only, which loads its scripts from a CDN.
    /// </summary>
    internal const string ApiReferencePath = "/api-docs";

    public static IServiceCollection AddSecurityHeaders(this IServiceCollection services) =>
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            // Sibling subdomains may be other apps still on plain HTTP, and preloading can't be undone quickly
            options.IncludeSubDomains = false;
            options.Preload = false;
        });

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        var isDevelopment = app.Environment.IsDevelopment();
        // Not in development, where pinning HTTPS on localhost would affect every other app served there
        if (!isDevelopment)
        {
            app.UseHsts();
        }

        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            if (!(isDevelopment && context.Request.Path.StartsWithSegments(ApiReferencePath)))
            {
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            }

            // Responses are only ever what their Content-Type says, so an uploaded script can't be run as a page's script
            headers.XContentTypeOptions = "nosniff";
            // Links out of Lookout, such as to a watched page, don't reveal its URLs
            headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });

        return app;
    }
}
