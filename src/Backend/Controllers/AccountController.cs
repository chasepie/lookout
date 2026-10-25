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

/// <summary>First-launch setup of the single local account, and logging in and out of it (ADR-0002).</summary>
[ApiController]
[Route("api")]
public sealed class AccountController(
    LookoutDbContext database,
    IPasswordHasher<Account> passwordHasher,
    IAntiforgery antiforgery,
    EventConnections eventConnections,
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
        var accountExists = await database.Accounts.AnyAsync(cancellationToken);
        return new SessionStatus(accountExists, User.Identity?.IsAuthenticated == true ? User.Identity.Name : null);
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup(Credentials credentials, CancellationToken cancellationToken)
    {
        var userName = credentials.UserName.Trim();
        if (userName.Length is < 1 or > 100)
        {
            return Problem("User name must contain between 1 and 100 characters.", statusCode: HttpStatusCode.BadRequest);
        }
        if (credentials.Password.Length is < 8 or > 1024)
        {
            return Problem("Password must contain between 8 and 1024 characters.", statusCode: HttpStatusCode.BadRequest);
        }
        if (await database.Accounts.AnyAsync(cancellationToken))
        {
            return AccountAlreadyExists();
        }

        var account = new Account { UserName = userName, PasswordHash = "" };
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
            return Problem("Set up Lookout's account before logging in.", statusCode: HttpStatusCode.Unauthorized);
        }

        // Check the password even for the wrong user name, so the response time doesn't reveal which was wrong.
        var verification = passwordHasher.VerifyHashedPassword(account, account.PasswordHash, credentials.Password);
        if (verification == PasswordVerificationResult.Failed
            || !string.Equals(account.UserName, credentials.UserName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Problem("Incorrect user name or password.", statusCode: HttpStatusCode.Unauthorized);
        }
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.HashPassword(account, credentials.Password);
        }

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

    private ObjectResult AccountAlreadyExists() =>
        Problem("Lookout's account is already set up. Log in instead.", statusCode: HttpStatusCode.Conflict);
}
