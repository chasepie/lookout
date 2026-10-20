using System.Security.Claims;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// The cookie that keeps the user logged in. It names a <see cref="Session"/>, and is honoured only while that
/// Session exists, so logging out ends the login even for a copy of the cookie.
/// </summary>
public static class SessionCookie
{
    private const string SessionIdClaim = "lookout:session";

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "Lookout.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Lookout is usually reached over plain HTTP on the LAN (ADR-0002); over HTTPS the cookie is Secure.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // A login lasts until logout: browsers cap cookie lifetimes at 400 days, and use renews it.
        options.ExpireTimeSpan = TimeSpan.FromDays(400);
        options.SlidingExpiration = true;
        // The API answers with status codes; the Angular app decides where to send the user.
        options.Events.OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden);
        options.Events.OnValidatePrincipal = RejectEndedSessions;
    }

    public static async Task SignInAsync(HttpContext context, LookoutDbContext database, Account account,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var session = new Session { AccountId = account.Id, CreatedAt = timeProvider.GetUtcNow() };
        database.Sessions.Add(session);
        await database.SaveChangesAsync(cancellationToken);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, account.UserName), new Claim(SessionIdClaim, session.Id.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    public static async Task SignOutAsync(HttpContext context, LookoutDbContext database, CancellationToken cancellationToken)
    {
        if (SessionId(context.User) is { } id)
        {
            await database.Sessions.Where(session => session.Id == id).ExecuteDeleteAsync(cancellationToken);
        }
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static async Task RejectEndedSessions(CookieValidatePrincipalContext context)
    {
        var database = context.HttpContext.RequestServices.GetRequiredService<LookoutDbContext>();
        var id = SessionId(context.Principal);
        if (id is null || !await database.Sessions.AnyAsync(session => session.Id == id, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Guid? SessionId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(SessionIdClaim), out var id) ? id : null;

    private static Task SetStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
