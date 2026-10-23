using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Boots the backend in-process against a temporary SQLite database.
/// Every host it builds, including those from <c>WithWebHostBuilder</c>, gets its own database,
/// and all of them are deleted when the factory is disposed.
/// Time stands still until a test advances <see cref="Time"/>.
/// </summary>
public sealed class LookoutWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly List<string> _databasePaths = [];
    public FakeRunnerClient Runner { get; } = new();
    public RecordingChannels Channels { get; } = new();
    /// <summary>Delivers notifications through the real Discord and Pushover senders instead of <see cref="Channels"/>.</summary>
    public bool UseRealChannels { get; init; }
    public FakeVpnRotator Vpn { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    /// <summary>Uses the real clock instead of <see cref="Time"/>, for a host that a browser uses interactively.</summary>
    public bool UseSystemTime { get; init; }
    /// <summary>Uses the VPN rotator Lookout's configuration chooses instead of <see cref="Vpn"/>.</summary>
    public bool UseConfiguredVpn { get; init; }
    /// <summary>
    /// Whether the scheduler starts due Runs on its own timer. Off, a test starts them with
    /// <see cref="Scheduling.Scheduler.StartDueRunsAsync"/>, so nothing races it.
    /// </summary>
    public bool PollsSchedule { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(RepositoryRoot.Find(), "src/Backend"));
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        _databasePaths.Add(databasePath);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Error");
        if (!PollsSchedule)
        {
            builder.UseSetting("Scheduling:Poll", "false");
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRunnerClient>(Runner);
            if (!UseConfiguredVpn)
            {
                services.AddSingleton<IVpnRotator>(Vpn);
            }
            services.AddSingleton(UseSystemTime ? TimeProvider.System : Time);
            if (!UseRealChannels)
            {
                services.RemoveAll<IChannelSender>();
                foreach (var sender in Channels.Senders())
                {
                    services.AddSingleton(sender);
                }
            }
        });
        builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False");
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _databasePaths.ForEach(File.Delete);
            GC.SuppressFinalize(this);
        }
    }
}
