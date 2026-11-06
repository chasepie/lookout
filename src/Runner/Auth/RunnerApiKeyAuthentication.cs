using System.Security.Claims;
using System.Text.Encodings.Web;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ChasePie.Lookout.Runner.Auth;

/// <summary>
/// Accepts only callers that send the <see cref="RunnerApiKey"/> in its header, which is to say the Backend. Every
/// endpoint requires it unless it allows anonymous access.
/// </summary>
/// <remarks>Why, and why the Runner must still not be reachable from outside: <c>docs/security.md</c>.</remarks>
public static class RunnerApiKeyAuthentication
{
    public const string SchemeName = "RunnerApiKey";

    public static IServiceCollection AddRunnerApiKeyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved here, so a missing or short key stops startup instead of failing every request
        services.AddSingleton(RunnerApiKey.FromConfiguration(configuration));

        services.AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, Handler>(SchemeName, null);

        services.AddAuthorizationBuilder()
            // Every endpoint requires the key unless it says otherwise, so a new one can't be left open by mistake
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    private sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        RunnerApiKey apiKey)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RunnerApiKey.HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // A repeated header is refused rather than picking one of its values
            if (values.Count != 1 || !apiKey.Matches(values[0]))
            {
                return Task.FromResult(AuthenticateResult.Fail($"Invalid {RunnerApiKey.HeaderName} header."));
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Backend")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
