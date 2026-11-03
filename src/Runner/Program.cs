
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Runner.Services;
using dotenv.net.Configuration;
using Scalar.AspNetCore;

namespace Runner;

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
        // Resolved here so a missing setting stops startup instead of failing each run
        services.AddSingleton(WatchScriptsDirectory.FromConfiguration(configuration));
        services.AddScoped<ScriptRunner>();
    }

    private static void ConfigureApp(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference("/api-docs");
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();
    }
}
