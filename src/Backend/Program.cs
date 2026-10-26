using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Backend.Security;
using ChasePie.Lookout.Backend.Startup;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();
        await LookoutDatabase.MigrateAsync(app);
        ConfigureApplication(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(
        IServiceCollection services,
        ConfigurationManager configuration,
        IWebHostEnvironment environment)
    {
        // Every POST, PUT, PATCH and DELETE to a controller must carry the request token (see RequestForgery).
        services.AddControllers(options => options.Filters.Add<RequestForgery.ValidateTokenFilter>());
        services.AddSingleton(TimeProvider.System);

        services.AddSignalR();
        services
            .AddSingleton<WatchEvents>()
            .AddSingleton<EventConnections>();

        services.AddHostedService<RunRetention>();
        services.AddScheduling(configuration);
        services.AddNotifications(configuration);
        services.AddRunner(configuration);
        services.AddVpn(configuration);
        services.AddOpenApi();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(SessionCookie.Configure);
        // Every endpoint requires a login unless it explicitly allows anonymous access.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(RequestForgery.Configure);
        services.AddDataProtection()
            .SetApplicationName("Lookout")
            .PersistKeysToDbContext<LookoutDbContext>();
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();

        services.AddLookoutDatabase(configuration, environment);
    }

    public static void ConfigureApplication(WebApplication app)
    {
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        // HTTPS comes from the user's own reverse proxy (ADR-0002), so a request it says came over HTTPS counts as one,
        // and its cookies are Secure. The proxy's address isn't known, so any client is trusted to say so: on the LAN,
        // the most one can do by lying is mark its own cookies Secure, or skip the redirect to HTTPS.
        var forwardedHeaders = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto
        };
        forwardedHeaders.KnownIPNetworks.Clear();
        forwardedHeaders.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeaders);
        // Health probes, such as a container's, check over HTTP and needn't follow a redirect.
        app.UseWhen(context =>
            !context.Request.Path.StartsWithSegments("/health"),
            branch => branch.UseHttpsRedirection());
        ContentSecurityPolicy.Apply(app);

        // The app's files are public: it shows setup or login itself, before the user has a session.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseAuthorization();
        RequestForgery.ProtectHub(app, WatchEventsHub.Path);

        app.MapControllers();
        app.MapHub<WatchEventsHub>(WatchEventsHub.Path);
        app.MapHealthChecks("/health").AllowAnonymous();
        // Keep API misses in endpoint routing, including 405 responses for unsupported methods.
        app.MapFallbackToFile("{*path:nonfile:regex(^(?!api(?:/|$)).*)}", "index.html").AllowAnonymous();
    }
}
