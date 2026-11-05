namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// Rejects requests that change data unless they come from Lookout's own pages: each POST, PUT, PATCH and DELETE must
/// carry <see cref="HeaderName"/> and a same-origin <c>Sec-Fetch-Site</c> (or, failing that, <c>Origin</c>). WebSocket
/// handshakes get the origin check only, since browsers can't add headers to them.
/// </summary>
/// <remarks>
/// Why it works this way instead of with antiforgery tokens, and what must stay true as Lookout changes:
/// <c>docs/security.md</c>.
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
