using System.Net;
using ChasePie.Lookout.Backend.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// Signing the Owner in and out.
/// </summary>
[ApiController]
[Route("api/auth")]
public partial class AuthController(OwnerCredentials owner, ILogger<AuthController> logger) : ControllerBase
{
    private const string IncorrectCredentialsMessage = "Username or password is incorrect.";

    private const string HttpsRequiredMessage =
        "Lookout must be served over HTTPS to sign in: browsers don't keep its session cookie over plain HTTP.";

    private readonly OwnerCredentials _owner = owner;
    private readonly ILogger<AuthController> _logger = logger;

    /// <summary>
    /// Signs the Owner in with a session cookie. Attempts are rate-limited per client. Fails with 400 over plain HTTP
    /// from anywhere but this machine, where the browser would drop the cookie.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [SignInEndpoint]
    [ProducesResponseType<OwnerInfo>(StatusCodes.Status200OK)]
    [ProducesResponseType<string>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<string>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OwnerInfo>> Login(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SignInRequest? request)
    {
        // Over plain HTTP the browser would drop the Secure cookie, so signing in would seem to work and then bounce
        // back to the login page. Browsers treat localhost as secure, so plain HTTP from this machine is fine.
        if (!Request.IsHttps && !IsLoopback(HttpContext.Connection.RemoteIpAddress))
        {
            return BadRequest(HttpsRequiredMessage);
        }

        var remoteIp = OwnerAuthentication.GetClientAddress(HttpContext);
        // Missing fields, or no body at all, get the same answer as wrong ones, so every failed sign-in looks alike
        if (!_owner.Matches(request?.Username ?? "", request?.Password ?? ""))
        {
            LogSignInFailed(remoteIp);
            return Unauthorized(IncorrectCredentialsMessage);
        }

        await HttpContext.SignInOwnerAsync(_owner);
        LogSignedIn(remoteIp);
        return new OwnerInfo(_owner.Username);
    }

    /// <summary>
    /// Ends the session. Works without one, so a client with an expired session can still clear its cookie.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutOwnerAsync();
        return NoContent();
    }

    /// <summary>
    /// The signed-in Owner. Fails with 401 when there's no session, which is how clients tell they're signed out.
    /// </summary>
    [HttpGet("me")]
    public ActionResult<OwnerInfo> Me() => new OwnerInfo(_owner.Username);

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Owner signed in from {RemoteIp}")]
    private partial void LogSignedIn(string remoteIp);

    // The attempted username isn't logged, in case a password was typed into it
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in from {RemoteIp}")]
    private partial void LogSignInFailed(string remoteIp);
}

/// <remarks>Nullable so missing fields get the same answer as wrong ones, not a validation error.</remarks>
public sealed record SignInRequest(string? Username, string? Password);

/// <param name="Username">The Owner's username, as configured.</param>
public sealed record OwnerInfo(string Username);
