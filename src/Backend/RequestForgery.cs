using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Protects state-changing requests from cross-site request forgery. SameSite cookies alone don't: browsers ignore ports
/// when judging "same site", and a homelab runs many services side by side on one host.
/// Every POST, PUT, PATCH and DELETE to a controller must carry the request token, which only Lookout's own app can read.
/// </summary>
public static class RequestForgery
{
    /// <summary>The cookie and header names Angular's <c>HttpClient</c> uses for XSRF tokens by default.</summary>
    private const string TokenCookieName = "XSRF-TOKEN";
    private const string TokenHeaderName = "X-XSRF-TOKEN";

    public static void Configure(AntiforgeryOptions options)
    {
        options.HeaderName = TokenHeaderName;
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
        context.Response.Cookies.Append(TokenCookieName, tokens.RequestToken!, new CookieOptions
        {
            // The app reads this cookie to send its value back in a header, which a page on another origin can't.
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps
        });
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
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            {
                return;
            }

            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                var problem = problemDetails.CreateProblemDetails(context.HttpContext, StatusCodes.Status400BadRequest,
                    detail: "This request is missing Lookout's request token. Reload the page and try again.");
                context.Result = new ObjectResult(problem) { StatusCode = problem.Status };
            }
        }
    }
}
