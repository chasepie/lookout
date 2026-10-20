using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        MigrateDatabase(app);
        ConfigureApplication(app);

        app.Run();
    }

    private static void ConfigureServices(IServiceCollection services, ConfigurationManager configuration)
    {
        // Every POST, PUT, PATCH and DELETE to a controller must carry the request token (see RequestForgery).
        services.AddControllers(options => options.Filters.Add<RequestForgery.ValidateTokenFilter>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RunningWatches>();
        var runnerUrl = configuration["Runner:BaseUrl"] ?? "http://localhost:3000";
        if (!Uri.TryCreate(runnerUrl.TrimEnd('/') + "/", UriKind.Absolute, out var runnerBaseAddress)
            || runnerBaseAddress.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Runner:BaseUrl must be an absolute HTTP or HTTPS URL, not '{runnerUrl}'");
        }
        services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
        {
            client.BaseAddress = runnerBaseAddress;
            // HttpRunnerClient sets each request's deadline from its Script timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddOpenApi();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(SessionCookie.Configure);
        // Every endpoint requires a login unless it explicitly allows anonymous access.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(RequestForgery.Configure);
        services.AddDataProtection().SetApplicationName("Lookout").PersistKeysToDbContext<LookoutDbContext>();
        services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();

        var connectionString = configuration.GetConnectionString("Lookout")
            ?? throw new InvalidOperationException("ConnectionStrings:Lookout is not configured");
        services.AddDbContext<LookoutDbContext>(options => options.UseSqlite(connectionString));
        services.AddHealthChecks()
            .AddDbContextCheck<LookoutDbContext>(customTestQuery: async (db, cancellationToken) =>
            {
                // CanConnect only checks that the SQLite file exists, and SQLite recreates a missing file empty;
                // reading the migrations history checks it's the database startup migrated.
                await db.Database.ExecuteSqlRawAsync("SELECT COUNT(*) FROM __EFMigrationsHistory", cancellationToken);
                return true;
            });
    }

    private static void MigrateDatabase(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.Migrate();
    }

    public static void ConfigureApplication(WebApplication app)
    {
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.UseHttpsRedirection();
        ContentSecurityPolicy.Apply(app);

        // The app's files are public: it shows setup or login itself, before the user has a session.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();
        app.MapHealthChecks("/health").AllowAnonymous();
        // Keep API misses in endpoint routing, including 405 responses for unsupported methods.
        app.MapFallbackToFile("{*path:nonfile:regex(^(?!api(?:/|$)).*)}", "index.html").AllowAnonymous();
    }
}
