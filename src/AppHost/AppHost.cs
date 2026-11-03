using dotenv.net;

namespace Chasepie.Lookout.AppHost;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Aspire starts each project from its own directory, where the services can't find the
        // repo-root .env, so forward the values they need from it.
        var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
        var dotEnv = DotEnv.Read(new DotEnvOptions(envFilePaths: [Path.Combine(repoRoot, ".env")]));

        var runner = builder.AddProject<Projects.Runner>("runner")
            .WithHttpHealthCheck("/health");

        var backend = builder.AddProject<Projects.Backend>("backend")
            .WithEnvironment("LOOKOUT_RUNNER_URL", runner.GetEndpoint("https"))
            .WithHttpHealthCheck("/health")
            .WaitFor(runner);

        if (dotEnv.TryGetValue("LOOKOUT_WATCH_SCRIPTS_DIR", out var watchScriptsDir) && !string.IsNullOrWhiteSpace(watchScriptsDir))
        {
            // Resolve a relative path against the repo root, where .env lives.
            backend.WithEnvironment("LOOKOUT_WATCH_SCRIPTS_DIR", Path.GetFullPath(watchScriptsDir, repoRoot));
        }

        // Runs `npm run start` (ng serve) on its default port. The dev server forwards /api to the
        // Backend through proxy.conf.json, which targets the Backend's launch profile port (7070).
        // Packages come from the root npm workspace, so run `npm install` at the repo root first.
        var frontend = builder.AddJavaScriptApp("frontend", "../frontend", "start")
            .WithNpm(install: false)
            .WithHttpEndpoint(port: 4200, isProxied: false)
            .WaitFor(backend);

        // Opens Chrome with the debugger attached when the AppHost is launched from VS Code, so
        // breakpoints in the Angular code are hit. The API is still marked experimental.
#pragma warning disable ASPIREEXTENSION001
        frontend.WithBrowserDebugger("chrome");
#pragma warning restore ASPIREEXTENSION001

        builder.Build().Run();
    }
}
