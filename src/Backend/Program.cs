using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Services;
using System.Threading.RateLimiting;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using dotenv.net.Configuration;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddDotNetEnv();
        builder.AddServiceDefaults();
        ConfigureServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        await ConfigureAppAsync(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOwnerAuthentication(configuration);
        services.AddControllers();
        services.AddOpenApi(options => options.AddDocumentTransformer<CsrfHeaderDocumentTransformer>());
        services.AddSignalR();
        services.AddSingleton<UpdatesNotifier>();
        // Directories are resolved here, so a missing setting stops startup instead of failing requests
        var connectionString = LookoutDbContext.GetConnectionString(configuration);
        services.AddDbContext<LookoutDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(WatchScriptsDirectory.FromConfiguration(configuration));
        services.AddScoped<WatchScriptManager>();
        services.AddScoped<DevelopmentDataSeeder>();
        services.AddHttpClient<RunnerHttpClient>(RunnerHttpClient.Configure);
        services.AddHostedService<WatchScheduler>();
        services.AddHostedService<WatchRunWorker>();

        var vpnConfiguration = VpnConfiguration.FromConfiguration(configuration);
        services.AddSingleton(vpnConfiguration);
        services.AddHttpClient<GluetunHttpClient>(GluetunHttpClient.Configure);
        services.AddHttpClient<VpnProbeHttpClient>().ConfigureVpnProbeHttpClient();
        services.AddSingleton<VpnRegionCatalog>();
        services.AddKeyedSingleton(VpnServiceKeys.Gate, (_, _) => new ConcurrencyLimiter(
            new ConcurrencyLimiterOptions
            {
                PermitLimit = 1,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
        // A singleton too, so the controller can queue switches on the instance that makes them
        services.AddSingleton<VpnRotationService>();
        services.AddHostedService(provider => provider.GetRequiredService<VpnRotationService>());
    }

    private static async Task ConfigureAppAsync(WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
        {
            // Creates the database on first start and applies any migrations added since.
            // A database made before migrations existed has no migrations history, so this fails on it; delete it.
            await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.MigrateAsync();

            if (app.Environment.IsDevelopment()
                && DevelopmentDataSeeder.FindExamplesDirectory(app.Environment.ContentRootPath) is { } examplesDir)
            {
                await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>()
                    .SeedAsync(examplesDir, app.Lifetime.ApplicationStopping);
            }
        }

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            // The docs are open so Scalar can be used signed out; its requests still need a session. Sign in by
            // calling /api/auth/login from Scalar, or in the frontend: the cookie works on every localhost port.
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference("/api-docs", options => options
                .AddPreferredSecuritySchemes(CsrfHeaderDocumentTransformer.SchemeName)
                .AddApiKeyAuthentication(CsrfHeaderDocumentTransformer.SchemeName, scheme => scheme.WithValue("1")))
                .AllowAnonymous();
        }

        app.UseHttpsRedirection();
        var hasFrontend = HasBuiltFrontend(app.Environment);
        if (hasFrontend)
        {
            // Before authentication, so the frontend's files load signed out
            app.UseStaticFiles();
        }

        app.UseCsrfProtection();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers();
        // Closing on expiry keeps a long-lived connection from outliving its session
        app.MapHub<UpdatesHub>(UpdatesHub.Path, options => options.CloseOnAuthenticationExpiration = true);
        MapFrontend(app, hasFrontend);
        app.MapDefaultEndpoints();
    }

    /// <summary>
    /// Whether <c>wwwroot</c> has the built frontend. Build it there with <c>npm run build -w @chasepie/lookout-frontend</c>.
    /// In development the Angular dev server serves the frontend instead.
    /// </summary>
    private static bool HasBuiltFrontend(IWebHostEnvironment environment) =>
        File.Exists(Path.Join(environment.ContentRootPath, "wwwroot", "index.html"));

    /// <summary>
    /// Serves the built frontend's <c>index.html</c> for every page's URL, so the frontend's routes load directly.
    /// </summary>
    private static void MapFrontend(WebApplication app, bool hasFrontend)
    {
        // Unknown API routes are 404s, not the frontend's page
        app.MapFallback("api/{**path}", () => Results.NotFound());

        if (hasFrontend)
        {
            // The page itself holds nothing private: the API calls it makes need a session
            app.MapFallbackToFile("index.html").AllowAnonymous();
        }
    }
}
