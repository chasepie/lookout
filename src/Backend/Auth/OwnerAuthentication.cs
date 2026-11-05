using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// Signing in as the Owner, the one person who uses Lookout, with a cookie session. Every endpoint requires the Owner
/// unless it allows anonymous access.
/// </summary>
/// <remarks>The reasoning behind the session, the fingerprint and the rate limits: <c>docs/security.md</c>.</remarks>
public static class OwnerAuthentication
{
    public const string CookieName = "lookout.session";

    /// <summary>
    /// How long a session lasts without being used. Each use extends it.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// Sign-in attempts allowed per client IP, and from all clients together, in each <see cref="SignInWindow"/>.
    /// </summary>
    public const int SignInsPerClient = 5;

    /// <inheritdoc cref="SignInsPerClient"/>
    public const int SignInsInTotal = 20;

    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(1);

    // Holds the credentials fingerprint the session signed in with
    private const string FingerprintClaimType = "lookout:credentials";

    private const string KeysDirectoryName = "keys";

    public static IServiceCollection AddOwnerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved here, so missing or weak credentials stop startup instead of failing sign-ins
        services.AddSingleton(OwnerCredentials.FromConfiguration(configuration));

        // Without persisted keys, every restart would sign out every session
        var databaseDir = LookoutEnvironmentVariables.GetRequiredDirectory(configuration, LookoutEnvironmentVariables.DatabaseDir);
        services.AddDataProtection()
            .SetApplicationName("Lookout")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Join(databaseDir, KeysDirectoryName)));

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = true;

                // An API: answer with status codes instead of redirecting to a login page
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = RejectOutdatedSessionAsync;
            });

        services.AddAuthorizationBuilder()
            // Every endpoint requires the Owner unless it says otherwise, so a new one can't be left open by mistake
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            // Sign-ins only, limited per client IP and in total
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsSignIn(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => SignInLimit(SignInsPerClient))
                    : RateLimitPartition.GetNoLimiter(string.Empty)),
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsSignIn(context)
                    ? RateLimitPartition.GetFixedWindowLimiter("all", _ => SignInLimit(SignInsInTotal))
                    : RateLimitPartition.GetNoLimiter(string.Empty)));
        });

        return services;
    }

    /// <summary>
    /// Signs the Owner in, with a session that lasts <see cref="SessionLifetime"/> past its last use.
    /// </summary>
    public static Task SignInOwnerAsync(this HttpContext context, OwnerCredentials owner)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, owner.Username),
                new Claim(FingerprintClaimType, owner.Fingerprint),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static Task SignOutOwnerAsync(this HttpContext context) =>
        context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    /// <summary>
    /// Ends a session that signed in with different credentials than the current ones.
    /// </summary>
    private static async Task RejectOutdatedSessionAsync(CookieValidatePrincipalContext context)
    {
        var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerCredentials>();
        var fingerprint = context.Principal?.FindFirstValue(FingerprintClaimType);
        if (!string.Equals(fingerprint, owner.Fingerprint, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutOwnerAsync();
        }
    }

    private static bool IsSignIn(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<SignInEndpointAttribute>() is not null;

    private static FixedWindowRateLimiterOptions SignInLimit(int permits) => new()
    {
        PermitLimit = permits,
        Window = SignInWindow,
        QueueLimit = 0,
    };
}

/// <summary>
/// Marks the sign-in endpoint, which <see cref="OwnerAuthentication"/> rate-limits.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SignInEndpointAttribute : Attribute;
