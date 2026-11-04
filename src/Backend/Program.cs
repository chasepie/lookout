using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using dotenv.net.Configuration;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddDotNetEnv();
        builder.AddServiceDefaults();
        ConfigureServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        ConfigureApp(app);

        app.Run();
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers();
        services.AddOpenApi();
        services.AddSignalR();
        services.AddSingleton<UpdatesNotifier>();
        // Directories are resolved here, so a missing setting stops startup instead of failing requests
        var connectionString = LookoutDbContext.GetConnectionString(configuration);
        services.AddDbContext<LookoutDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(WatchScriptsDirectory.FromConfiguration(configuration));
        services.AddScoped<WatchScriptManager>();
        services.AddHttpClient<RunnerHttpClient>(RunnerHttpClient.Configure);
        services.AddHostedService<WatchScheduler>();
        services.AddHostedService<WatchRunWorker>();

        var vpnConfiguration = VpnConfiguration.FromConfiguration(configuration);
        services.AddSingleton(vpnConfiguration);
        services.AddHttpClient<GluetunHttpClient>(GluetunHttpClient.Configure);
        if (vpnConfiguration.ProxyUrl is not null)
        {
            VpnProxyProbe.Configure(services.AddHttpClient<VpnProxyProbe>());
        }
        services.AddSingleton<VpnRegionCatalog>();
        services.AddSingleton<VpnGate>();
        // A singleton too, so the controller can queue switches on the instance that makes them
        services.AddSingleton<VpnRotationService>();
        services.AddHostedService(provider => provider.GetRequiredService<VpnRotationService>());
    }

    private static void ConfigureApp(WebApplication app)
    {
        // Creates the database on first start and applies any migrations added since.
        // A database made before migrations existed has no migrations history, so this fails on it; delete it.
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.Migrate();
        }

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference("/api-docs");
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.MapHub<UpdatesHub>(UpdatesHub.Path);
        app.MapDefaultEndpoints();
    }
}
