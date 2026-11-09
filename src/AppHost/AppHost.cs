namespace ChasePie.Lookout.AppHost;

/// <summary>
/// Runs Lookout for development (<c>dotnet run</c>), or describes its production deployment (<c>aspire publish</c>), which
/// writes the Docker Compose files in <c>deploy/</c>. The two are built separately: in development the Backend and the
/// frontend run on this machine where they can be debugged, while in production every service is a container on networks
/// of its own. See <c>Development.cs</c> and <c>Production.cs</c>.
/// </summary>
public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        if (builder.ExecutionContext.IsPublishMode)
        {
            Production.Add(builder);
        }
        else
        {
            Development.Add(builder);
        }

        builder.Build().Run();
    }
}
