using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using dotenv.net.Configuration;
using Scalar.AspNetCore;

namespace ChasePie.Lookout.Backend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddDotNetEnv();
        builder.AddServiceDefaults();
        ConfigureServices(builder.Services);

        var app = builder.Build();
        ConfigureApp(app);

        app.Run();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        services.AddOpenApi();
        services.AddDbContext<LookoutDbContext>();
        services.AddScoped<WatchScriptManager>();
        services.AddHttpClient<RunnerHttpClient>(RunnerHttpClient.Configure);
    }

    private static void ConfigureApp(WebApplication app)
    {
        // There are no migrations yet, so create the schema directly.
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.EnsureCreated();
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
        app.MapDefaultEndpoints();
    }
}
