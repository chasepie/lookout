using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace ChasePie.Lookout.Backend.Security;

/// <summary>
/// Protects state-changing requests from cross-site request forgery. SameSite cookies alone don't: browsers ignore ports
/// when judging "same site", and a homelab runs many services side by side on one host.
/// Every POST, PUT, PATCH and DELETE to a controller must carry the request token in a header, which only Lookout's own
/// app can send.
/// </summary>
public static class RequestForgery
{
    /// <summary>
    /// The cookie the app reads the request token from. Every port on a host shares cookies, so Angular's default
    /// <c>XSRF-TOKEN</c> would collide with other Angular apps in the homelab; the app is configured with this name.
    /// </summary>
    public const string TokenCookieName = "Lookout.XSRF-TOKEN";

    /// <summary>The header Angular's <c>HttpClient</c> sends the request token in by default.</summary>
    public const string TokenHeaderName = "X-XSRF-TOKEN";

    /// <summary>Identifies a rejected request token, so the app can fetch a fresh one and retry.</summary>
    private const string MissingTokenProblemType = "urn:lookout:problem:request-token";

    public static void Configure(AntiforgeryOptions options)
    {
        options.HeaderName = TokenHeaderName;
        // A page on another port can read the token cookie, and could otherwise post it as a form field, which needs no
        // CORS preflight. Only the header can't be sent cross-origin without one, which Lookout never answers.
        options.SuppressReadingTokenFromFormBody = true;
        options.Cookie.Name = "Lookout.Antiforgery";
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    }

    /// <summary>
    /// Gives the app a request token for the current user. The token is tied to the login it was issued for, so the app
    /// fetches a new one with the session status after every setup, login and logout.
    /// </summary>
    public static void IssueToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(
            key: TokenCookieName,
            value: tokens.RequestToken!,
            options: new CookieOptions
            {
                // The app reads this cookie to send its value back in a header. A page on another port of the host can
                // read it too, but can't send that header to Lookout.
                HttpOnly = false,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps
            });
    }

    /// <summary>
    /// Protects a SignalR hub from cross-site WebSocket hijacking, which would let a page on another port read what it
    /// pushes. Browsers send cookies, but no headers, when opening a WebSocket, so it can't carry the request token.
    /// Instead, negotiating a connection must carry it, and every other request to the hub must belong to a negotiated
    /// connection. A page on another origin can send a negotiation but can't read the connection ID in the response.
    /// </summary>
    public static void ProtectHub(WebApplication app, PathString path)
    {
        app.UseWhen(context =>
            context.Request.Path.StartsWithSegments(path),
            branch => branch.Use(async (context, next) =>
            {
                var negotiating = context.Request.Path.StartsWithSegments(path.Add("/negotiate"));
                if (negotiating && !await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
                {
                    await Results
                        .Problem(
                            type: MissingTokenProblemType,
                            statusCode: StatusCodes.Status400BadRequest,
                            detail: "This connection is missing Lookout's request token. Reload the page and try again.")
                        .ExecuteAsync(context);
                    return;
                }

                // The same test SignalR applies to the connection ID, which starts a new connection when it's missing or empty.
                if (!negotiating && StringValues.IsNullOrEmpty(context.Request.Query["id"]))
                {
                    await Results
                        .Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            detail: "Negotiate a connection before connecting.")
                        .ExecuteAsync(context);
                    return;
                }
                await next(context);
            }));
    }

    /// <summary>
    /// Rejects a controller request that changes state without a valid request token. It applies the same rule as MVC's
    /// <see cref="AutoValidateAntiforgeryTokenAttribute"/>, which needs the view services an API-only app doesn't register.
    /// </summary>
    public sealed class ValidateTokenFilter(IAntiforgery antiforgery, ProblemDetailsFactory problemDetails) : IAsyncAuthorizationFilter
    {
        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method)
                || HttpMethods.IsHead(method)
                || HttpMethods.IsOptions(method)
                || HttpMethods.IsTrace(method))
            {
                return;
            }

            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                var problem = problemDetails.CreateProblemDetails(
                    httpContext: context.HttpContext,
                    statusCode: StatusCodes.Status400BadRequest,
                    type: MissingTokenProblemType,
                    detail: "This request is missing Lookout's request token. Reload the page and try again.");
                context.Result = new ObjectResult(problem)
                {
                    StatusCode = problem.Status
                };
            }
        }
    }
}
