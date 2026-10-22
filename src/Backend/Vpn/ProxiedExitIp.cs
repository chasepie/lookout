using System.Net;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>
/// The VPN's exit IP as a page loaded through its proxy sees it, for when gluetun's own lookup fails though the tunnel
/// is up (ADR-0004). Lookout shares no network with the proxy, as pages loaded through it could then reach Lookout, so
/// the page is loaded the way a VPN Run's are: by the runner, in Browserless. A proxy whose tunnel is down holds requests until they time out, so the
/// check only follows <see cref="CheckAfter"/> without an exit IP from gluetun, and at most that often.
/// </summary>
/// <param name="runner">The runner, resolved for each check, as its HTTP client is short-lived.</param>
public sealed class ProxiedExitIp(Func<IRunnerClient> runner, TimeProvider timeProvider)
{
    /// <summary>
    /// How long gluetun must have had no exit IP before a check, and how long between checks. Short enough for one to
    /// fit within an attempt at a Rotation.
    /// </summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromSeconds(10);

    private const string EchoUrl = "https://api.ipify.org/";

    /// <summary>Reads the echo page's text, which is the IP it was requested from.</summary>
    private const string CheckSource = """
        export default {
          async run(page, context) {
            await page.goto(context.notificationUrl);
            return { success: true, content: (await page.locator('body').innerText()).trim() };
          },
        };
        """;

    private readonly Lock _lock = new();
    private DateTimeOffset? _noneSince;
    private DateTimeOffset? _lastCheckAt;

    /// <summary>Notes that gluetun has an exit IP of its own, or no tunnel at all, so no check is needed.</summary>
    public void NotNeeded()
    {
        lock (_lock)
        {
            _noneSince = null;
        }
    }

    /// <summary>
    /// The exit IP, if gluetun's VPN is running without one of its own and a check through <paramref name="proxy"/>
    /// is due and finds one. Otherwise nothing.
    /// </summary>
    public async Task<string?> CheckAsync(ProxySettings proxy, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            _noneSince ??= now;
            if (now - _noneSince < CheckAfter || now - _lastCheckAt < CheckAfter)
            {
                return null;
            }
            _lastCheckAt = now;
        }
        try
        {
            var result = await runner().ExecuteAsync(new RunnerRequest(
                Source: CheckSource,
                FileKind: "ts",
                Context: new ScriptContext(EchoUrl),
                TimeoutMs: (int)CheckAfter.TotalMilliseconds / 2,
                Proxy: proxy), cancellationToken);
            return result.Outcome == RunOutcome.Succeeded && IPAddress.TryParse(result.Content?.Trim(), out var ip)
                ? ip.ToString()
                : null;
        }
        // Anything but the caller cancelling means no exit IP was found.
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
