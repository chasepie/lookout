using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Runner;
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
        services.AddControllers();
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
        app.UseAuthorization();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapControllers();
        app.MapHealthChecks("/health");
        // Keep API misses in endpoint routing, including 405 responses for unsupported methods.
        app.MapFallbackToFile("{*path:nonfile:regex(^(?!api(?:/|$)).*)}", "index.html");
    }
}
