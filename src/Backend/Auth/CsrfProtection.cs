namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// Rejects requests that change data unless they provably come from Lookout's own pages. On top of the session
/// cookie's <c>SameSite=Strict</c>, each POST, PUT, PATCH and DELETE must:
/// <list type="number">
/// <item>carry the <see cref="HeaderName"/> header, which a page on another origin can't add without a CORS preflight
/// that the Backend never allows; and</item>
/// <item>carry <c>Sec-Fetch-Site: same-origin</c>, or, from a browser without Fetch Metadata, an <c>Origin</c> that is
/// the request's own. Browsers set both headers and pages can't change them. A request with neither is rejected:
/// every browser sends one, and the browser is Lookout's only client.</item>
/// </list>
/// WebSocket handshakes, such as the updates hub's, get the second check only, since browsers can't add headers to
/// them. They need it because WebSockets aren't covered by CORS: without it, a page on a sibling subdomain, which is
/// sent the <c>SameSite=Strict</c> cookie, could open the hub and listen in.
/// </summary>
/// <remarks>
/// This replaces antiforgery tokens on purpose. With no CORS policy, tokens add no protection over these checks, and
/// issuing and refreshing them in the SPA is where token setups usually go wrong. Go's <c>http.CrossOriginProtection</c>
/// works the same way (https://words.filippo.io/csrf/), and the OWASP CSRF Prevention Cheat Sheet lists both custom
/// request headers and Fetch Metadata as defences. If Lookout ever accepts API tokens, apply these checks only to
/// requests authenticated by the cookie: browsers don't send tokens on their own, so those requests can't be forged.
/// Behind a reverse proxy that rewrites <c>Host</c>, the <c>Origin</c> fallback needs forwarded headers configured.
/// </remarks>
public static class CsrfProtection
{
    public const string HeaderName = "X-Lookout-Request";

    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (GetRejection(context.Request) is { } reason)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(reason, context.RequestAborted);
                return;
            }

            await next(context);
        });

    /// <returns>Why the request is rejected, or <see langword="null"/> if it's allowed.</returns>
    public static string? GetRejection(HttpRequest request)
    {
        if (IsWebSocketHandshake(request))
        {
            return GetCrossOriginRejection(request);
        }

        var method = request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
        {
            return null;
        }

        if (!request.Headers.ContainsKey(HeaderName))
        {
            return $"Requests that change data must send the {HeaderName} header.";
        }

        return GetCrossOriginRejection(request);
    }

    /// <summary>
    /// A WebSocket opening over HTTP/1.1 (a GET asking to upgrade) or over HTTP/2 (an extended CONNECT, which Kestrel
    /// only accepts for WebSockets).
    /// </summary>
    private static bool IsWebSocketHandshake(HttpRequest request) =>
        HttpMethods.IsConnect(request.Method)
        || (HttpMethods.IsGet(request.Method)
            && request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase));

    /// <returns>Why the request isn't provably from Lookout's own origin, or <see langword="null"/> if it is.</returns>
    private static string? GetCrossOriginRejection(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
        {
            return fetchSite == "same-origin"
                ? null
                : "Requests must come from Lookout itself (Sec-Fetch-Site isn't same-origin).";
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return "Requests must send a Sec-Fetch-Site or Origin header.";
        }

        return string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Requests must come from Lookout itself (Origin doesn't match).";
    }
}
