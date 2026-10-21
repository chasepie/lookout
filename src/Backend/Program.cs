using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();
        MigrateDatabase(app);
        ConfigureApplication(app);

        app.Run();
    }

    private static void ConfigureServices(
        IServiceCollection services, ConfigurationManager configuration, IWebHostEnvironment environment)
    {
        // Every POST, PUT, PATCH and DELETE to a controller must carry the request token (see RequestForgery).
        services.AddControllers(options => options.Filters.Add<RequestForgery.ValidateTokenFilter>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RunningWatches>();
        services.AddScoped<RunExecutor>();
        ConfigureScheduling(services, configuration);
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

        var connectionString = DatabaseConnectionString(configuration, environment);
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

    /// <summary>
    /// The database's connection string, with a relative path resolved against the data directory
    /// (<c>Storage:DataDirectory</c>, by default the content root), which is created if it's missing. SQLite would
    /// resolve it against the working directory, which depends on how the app was started; in a container, that's
    /// lost with the container unless a volume happens to be mounted there.
    /// </summary>
    private static string DatabaseConnectionString(ConfigurationManager configuration, IWebHostEnvironment environment)
    {
        var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("Lookout")
            ?? throw new InvalidOperationException("ConnectionStrings:Lookout is not configured"));
        if (connection.DataSource is "" or ":memory:" || connection.Mode == SqliteOpenMode.Memory)
        {
            return connection.ConnectionString;
        }
        var dataDirectory = Path.GetFullPath(configuration["Storage:DataDirectory"] ?? ".", environment.ContentRootPath);
        connection.DataSource = Path.GetFullPath(connection.DataSource, dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
        return connection.ConnectionString;
    }

    private static void ConfigureScheduling(IServiceCollection services, ConfigurationManager configuration)
    {
        var timeZoneId = configuration["Scheduling:TimeZone"];
        TimeZoneInfo? timeZone = null;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out timeZone))
        {
            throw new InvalidOperationException($"Scheduling:TimeZone must be a time zone ID such as Europe/London, not '{timeZoneId}'");
        }
        services.AddSingleton(provider => new WatchSchedules(provider.GetRequiredService<TimeProvider>(), timeZone));

        var maxConcurrentRuns = configuration.GetValue("Scheduling:MaxConcurrentRuns", 2);
        if (maxConcurrentRuns < 1)
        {
            throw new InvalidOperationException($"Scheduling:MaxConcurrentRuns must be at least 1, not {maxConcurrentRuns}");
        }
        services.AddSingleton(_ => new RunSlots(maxConcurrentRuns));
        services.AddHostedService<Scheduler>();
    }

    private static void MigrateDatabase(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        // Seeding only a database this startup creates keeps a removed example from coming back.
        var created = !database.Database.GetAppliedMigrations().Any();
        database.Database.Migrate();
        if (created && app.Environment.IsDevelopment())
        {
            ExampleWatches.Seed(database, scope.ServiceProvider.GetRequiredService<WatchSchedules>());
        }
    }

    public static void ConfigureApplication(WebApplication app)
    {
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        // Health probes, such as a container's, check over HTTP and needn't follow a redirect.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
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
