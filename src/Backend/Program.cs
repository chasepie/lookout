using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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
        services.AddSignalR();
        services.AddSingleton<WatchEvents>();
        services.AddSingleton<EventConnections>();
        services.AddScoped<RunExecutor>();
        services.AddHostedService<RunRetention>();
        ConfigureScheduling(services, configuration);
        ConfigureNotifications(services, configuration);
        var runnerBaseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Runner:BaseUrl", "http://localhost:3000");
        services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
        {
            client.BaseAddress = runnerBaseAddress;
            // HttpRunnerClient sets each request's deadline from its Script timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        ConfigureVpn(services, configuration);
        services.AddSingleton<VpnRotation>();
        services.AddHostedService(provider => provider.GetRequiredService<VpnRotation>());
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
    /// lost with the container unless a volume happens to be mounted there. A URI, an in-memory database, or a path
    /// using Microsoft.Data.Sqlite's <c>|DataDirectory|</c> is left as given.
    /// </summary>
    private static string DatabaseConnectionString(ConfigurationManager configuration, IWebHostEnvironment environment)
    {
        var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("Lookout")
            ?? throw new InvalidOperationException("ConnectionStrings:Lookout is not configured"));
        if (connection.DataSource is "" or ":memory:"
            || connection.Mode == SqliteOpenMode.Memory
            || connection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || connection.DataSource.StartsWith("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
        {
            return connection.ConnectionString;
        }
        var dataDirectory = Path.GetFullPath(configuration["Storage:DataDirectory"] ?? ".", environment.ContentRootPath);
        connection.DataSource = Path.GetFullPath(connection.DataSource, dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
        return connection.ConnectionString;
    }

    /// <summary>
    /// gluetun, when Vpn:ApiKey is set: its control server requires a key on every route Lookout uses (ADR-0004).
    /// Without one, the VPN is disabled and VPN Runs are refused.
    /// </summary>
    private static void ConfigureVpn(IServiceCollection services, ConfigurationManager configuration)
    {
        var apiKey = configuration["Vpn:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            services.AddSingleton<IVpnRotator, DisabledVpnRotator>();
            return;
        }
        // Deployment writes the key into gluetun's TOML auth file, where quotes or backslashes would change it.
        if (!apiKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InvalidOperationException("Vpn:ApiKey must contain only letters, digits, '-' and '_', such as from openssl rand -hex 32");
        }
        var controlAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Vpn:ControlUrl", "http://gluetun-control:8000");
        var proxyAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Vpn:ProxyUrl", "http://gluetun:8888");
        // Chromium doesn't use credentials written into the proxy's address.
        if (proxyAddress.UserInfo != "")
        {
            throw new InvalidOperationException("Vpn:ProxyUrl must not contain credentials; set Vpn:ProxyUsername and Vpn:ProxyPassword instead");
        }
        // A proxy is a host and port; ConfiguredBaseAddress adds the trailing slash.
        if (proxyAddress.AbsolutePath != "/" || proxyAddress.Query != "")
        {
            throw new InvalidOperationException($"Vpn:ProxyUrl must be only a scheme, host and port, such as http://gluetun:8888, not '{configuration["Vpn:ProxyUrl"]}'");
        }
        var proxy = new ProxySettings(
            Server: proxyAddress.GetLeftPart(UriPartial.Authority),
            Username: NullIfEmpty(configuration["Vpn:ProxyUsername"]),
            Password: NullIfEmpty(configuration["Vpn:ProxyPassword"]));
        services.AddSingleton(new GluetunSettings(apiKey, proxy));
        services.AddHttpClient<IVpnRotator, GluetunVpnRotator>(client =>
        {
            client.BaseAddress = controlAddress;
            // Reading the status has a shorter deadline of its own (GluetunSettings.StatusTimeout); this bounds a Rotation.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
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

    private static void ConfigureNotifications(IServiceCollection services, ConfigurationManager configuration)
    {
        var pushoverBaseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Notifications:PushoverBaseUrl",
            PushoverSender.DefaultBaseUrl);

        // A sender's request URL or body holds its Channel's secrets, so its requests are never logged. A notification
        // is sent while its Run holds a slot, so a Channel's service that doesn't answer is given up on soon.
        var sendTimeout = TimeSpan.FromSeconds(10);
        services.AddHttpClient(nameof(DiscordSender), client => client.Timeout = sendTimeout).RemoveAllLoggers();
        services.AddHttpClient(nameof(PushoverSender), client =>
        {
            client.BaseAddress = pushoverBaseAddress;
            client.Timeout = sendTimeout;
        }).RemoveAllLoggers();
        services.AddTransient<IChannelSender>(provider =>
            new DiscordSender(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DiscordSender))));
        services.AddTransient<IChannelSender>(provider =>
            new PushoverSender(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PushoverSender))));
        services.AddScoped<NotificationDispatcher>();
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

        // HTTPS comes from the user's own reverse proxy (ADR-0002), so a request it says came over HTTPS counts as one,
        // and its cookies are Secure. The proxy's address isn't known, so any client is trusted to say so: on the LAN,
        // the most one can do by lying is mark its own cookies Secure, or skip the redirect to HTTPS.
        var forwardedHeaders = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
        forwardedHeaders.KnownIPNetworks.Clear();
        forwardedHeaders.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeaders);
        // Health probes, such as a container's, check over HTTP and needn't follow a redirect.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
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
