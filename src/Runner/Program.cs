
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Runner.Auth;
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
        services.AddRunnerApiKeyAuthentication(configuration);
        services.AddControllers();
        services.AddOpenApi(options => options.AddDocumentTransformer<RunnerApiKeyDocumentTransformer>());
        // Resolved here so a missing setting stops startup instead of failing each run
        services.AddSingleton(WatchScriptsDirectory.FromConfiguration(configuration));
        services.AddScoped<ScriptRunner>();
    }

    private static void ConfigureApp(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            // Open, but requests made from Scalar still need the key; see docs/security.md
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference("/api-docs", options => options
                .AddPreferredSecuritySchemes(RunnerApiKeyDocumentTransformer.SchemeName))
                .AllowAnonymous();
        }

        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();
    }
}
