using System.Globalization;
using System.Net;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Security;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record SessionStatus(bool AccountExists, string? UserName);

public sealed record Credentials(string UserName, string Password);

/// <summary>First-launch setup of the single local account, and logging in and out of it (ADR-0002).</summary>
[ApiController]
[Route("api")]
public sealed class AccountController(
    LookoutDbContext database,
    IPasswordHasher<Account> passwordHasher,
    IAntiforgery antiforgery,
    EventConnections eventConnections,
    LoginLimit loginLimit,
    TimeProvider timeProvider)
    : LookoutControllerBase
{
    /// <summary>
    /// Tells the app whether to show setup, login, or the logged-in user's Watches, and gives it the request token
    /// for this login.
    /// </summary>
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<SessionStatus> Status(CancellationToken cancellationToken)
    {
        RequestForgery.IssueToken(HttpContext, antiforgery);
        return new SessionStatus(
            AccountExists: await database.Accounts.AnyAsync(cancellationToken),
            UserName: User.Identity?.IsAuthenticated == true ? User.Identity.Name : null);
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup(Credentials credentials, CancellationToken cancellationToken)
    {
        if (await database.Accounts.AnyAsync(cancellationToken))
        {
            return AccountAlreadyExists();
        }

        if (!Account.IsValidUserName(credentials.UserName))
        {
            return Problem(
                detail: $"User name must contain between {Account.MinimumUserNameLength} and {Account.MaximumUserNameLength} characters.",
                statusCode: HttpStatusCode.BadRequest);
        }

        if (!Account.IsValidPassword(credentials.Password))
        {
            return Problem(
                detail: $"Password must contain between {Account.MinimumPasswordLength} and {Account.MaximumPasswordLength} characters.",
                statusCode: HttpStatusCode.BadRequest);
        }

        var account = new Account
        {
            UserName = credentials.UserName.Trim(),
            PasswordHash = ""
        };
        account.PasswordHash = passwordHasher.HashPassword(account, credentials.Password);
        database.Accounts.Add(account);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another setup request created the Account first; the singleton Id refused this one.
            return AccountAlreadyExists();
        }

        await SessionCookie.SignInAsync(HttpContext, database, account, timeProvider, cancellationToken);
        return StatusCode(StatusCodes.Status201Created);
    }

    [HttpPost("session")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(Credentials credentials, CancellationToken cancellationToken)
    {
        var account = await database.Accounts.SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return Problem(
                detail: "Set up Lookout's account before logging in.",
                statusCode: HttpStatusCode.Unauthorized);
        }

        var client = HttpContext.Connection.RemoteIpAddress;
        if (loginLimit.TryStart(client) is { } wait)
        {
            return TooManyFailedLogins(wait);
        }

        // Check the password even for the wrong user name, so the response time doesn't reveal which was wrong.
        var verification = passwordHasher.VerifyHashedPassword(
            user: account,
            hashedPassword: account.PasswordHash,
            providedPassword: credentials.Password);
        if (verification == PasswordVerificationResult.Failed
            || !string.Equals(account.UserName, credentials.UserName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                detail: "Incorrect user name or password.",
                statusCode: HttpStatusCode.Unauthorized);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.HashPassword(account, credentials.Password);
        }

        loginLimit.Succeeded(client);
        await SessionCookie.SignInAsync(HttpContext, database, account, timeProvider, cancellationToken);
        return NoContent();
    }

    [HttpDelete("session")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var sessionId = SessionCookie.SessionId(User);
        await SessionCookie.SignOutAsync(HttpContext, database, cancellationToken);
        if (sessionId is { } id)
        {
            eventConnections.Close(id);
        }
        return NoContent();
    }

    private ObjectResult TooManyFailedLogins(TimeSpan wait)
    {
        var minutes = (int)Math.Ceiling(wait.TotalMinutes);
        Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Problem(
            detail: $"Too many failed logins. Try again in {minutes} {(minutes == 1 ? "minute" : "minutes")}.",
            statusCode: HttpStatusCode.TooManyRequests);
    }

    private ObjectResult AccountAlreadyExists() =>
        Problem("Lookout's account is already set up. Log in instead.", statusCode: HttpStatusCode.Conflict);
}
