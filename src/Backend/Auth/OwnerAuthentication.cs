using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
    // The __Host- prefix makes browsers refuse a cookie of this name that's scoped to a parent domain or a narrower
    // path, so a sibling subdomain can't plant one that shadows the session
    public const string CookieName = "__Host-lookout.session";

    /// <summary>
    /// How long a session lasts without being used. Each use extends it.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// Sign-in attempts allowed per client in each <see cref="SignInWindow"/>; see <see cref="GetRateLimitKey"/>.
    /// </summary>
    public const int SignInsPerClient = 5;

    public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(1);

    // The credentials fingerprint and session generation the session signed in with
    private const string FingerprintClaimType = "lookout:credentials";
    private const string GenerationClaimType = "lookout:generation";

    private const string KeysDirectoryName = "keys";

    public static IServiceCollection AddOwnerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved here, so missing or weak credentials stop startup instead of failing sign-ins
        services.AddSingleton(OwnerCredentials.FromConfiguration(configuration));
        services.AddSingleton<OwnerSessions>();

        // Without persisted keys, every restart would sign out every session
        var databaseDir = EnvVars.GetRequiredDirectory(configuration, EnvVars.LookoutDatabaseDir);
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
            // Sign-ins only, per client. There's deliberately no limit across all clients: an attacker with a few
            // addresses could keep it exhausted and lock the Owner out.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsSignIn(context)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    GetRateLimitKey(context.Connection.RemoteIpAddress), _ => SignInLimit(SignInsPerClient))
                : RateLimitPartition.GetNoLimiter(string.Empty));
        });

        return services;
    }

    /// <summary>
    /// Signs the Owner in, with a session that lasts <see cref="SessionLifetime"/> past its last use.
    /// </summary>
    public static Task SignInOwnerAsync(this HttpContext context, OwnerCredentials owner)
    {
        var generation = context.RequestServices.GetRequiredService<OwnerSessions>().Generation;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, owner.Username),
                new Claim(FingerprintClaimType, owner.Fingerprint),
                new Claim(GenerationClaimType, generation.ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// Clears this client's cookie. If the request has a session, also ends every session, so a copy of the cookie
    /// stops working too. Without a session it only clears the cookie, so signed-out callers can't sign the Owner out.
    /// </summary>
    public static async Task SignOutOwnerAsync(this HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await context.RequestServices.GetRequiredService<OwnerSessions>().EndAllAsync(context.RequestAborted);
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Ends a session that signed in with different credentials than the current ones, or before the last sign-out.
    /// </summary>
    private static async Task RejectOutdatedSessionAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var fingerprint = context.Principal?.FindFirstValue(FingerprintClaimType);
        var generation = context.Principal?.FindFirstValue(GenerationClaimType);
        var isCurrent =
            string.Equals(fingerprint, services.GetRequiredService<OwnerCredentials>().Fingerprint, StringComparison.Ordinal)
            && string.Equals(
                generation,
                services.GetRequiredService<OwnerSessions>().Generation.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        if (!isCurrent)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>
    /// The client's address, as the Backend sees it, for logs. Behind a reverse proxy this needs forwarded headers.
    /// </summary>
    public static string GetClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Which client a sign-in counts against: the IPv4 address, or the IPv6 /64 network, since one IPv6 client
    /// usually controls a whole /64 and could otherwise use a fresh address for every attempt.
    /// </summary>
    public static string GetRateLimitKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
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
