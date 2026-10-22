using System.Net;
using System.Net.Sockets;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>
/// The VPN's exit IP as a page loaded through its proxy sees it, for when gluetun's own lookup fails though the tunnel
/// is up (ADR-0004). Lookout shares no network with the proxy, as pages loaded through it could then reach Lookout, so
/// the page is loaded the way a VPN Run's are: by the runner, in Browserless. A proxy whose tunnel is down holds
/// requests until they time out, so the check only follows <see cref="CheckAfter"/> without an exit IP from gluetun,
/// and at most that often.
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

    /// <summary>How long a check may take: a Browserless session, which may queue, and loading the page.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private DateTimeOffset? _noneSince;
    private DateTimeOffset? _lastCheckAt;
    /// <summary>The exit IP the last check found, which every read uses until it's <see cref="CheckAfter"/> old.</summary>
    private (string Ip, DateTimeOffset FoundAt)? _found;
    /// <summary>The check under way, which reads meanwhile wait for rather than starting another.</summary>
    private Task<string?>? _checking;
    /// <summary>How many times it has been told to forget, so a check that was under way then doesn't keep its IP.</summary>
    private long _forgotten;

    /// <summary>
    /// Forgets the exit IP found and how long gluetun has had none, as gluetun has an exit IP of its own, no tunnel,
    /// or is reconnecting to a new exit.
    /// </summary>
    public void Forget()
    {
        lock (_lock)
        {
            _noneSince = null;
            _found = null;
            _forgotten++;
        }
    }

    /// <summary>
    /// The exit IP, while gluetun's VPN is running without one of its own: as the last check through
    /// <paramref name="proxy"/> found it, or a check due now finds it. Otherwise nothing.
    /// </summary>
    public async Task<string?> CheckAsync(ProxySettings proxy, CancellationToken cancellationToken)
    {
        Task<string?> checking;
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            _noneSince ??= now;
            if (_found is { } found && now - found.FoundAt < CheckAfter)
            {
                return found.Ip;
            }
            if (_checking is null)
            {
                if (now - _noneSince < CheckAfter || now - _lastCheckAt < CheckAfter)
                {
                    return null;
                }
                _lastCheckAt = now;
                _checking = CheckThroughProxyAsync(proxy, _forgotten);
            }
            checking = _checking;
        }
        return await checking.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the echo page through the proxy, on a deadline of its own rather than its first caller's, as other reads
    /// wait for it too.
    /// </summary>
    private async Task<string?> CheckThroughProxyAsync(ProxySettings proxy, long forgottenBefore)
    {
        // Its caller notes it as under way while holding the lock, which it takes below, so it only ends after that.
        await Task.Yield();
        string? ip = null;
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout, timeProvider);
            var result = await runner().ExecuteAsync(new RunnerRequest(
                Source: CheckSource,
                FileKind: "ts",
                Context: new ScriptContext(EchoUrl),
                TimeoutMs: (int)(CheckTimeout / 2).TotalMilliseconds,
                Proxy: proxy), timeout.Token);
            ip = result.Outcome == RunOutcome.Succeeded ? IpIn(result.Content) : null;
        }
        // Any failure means no exit IP was found.
        catch (Exception)
        {
        }
        lock (_lock)
        {
            _checking = null;
            if (ip is not null && forgottenBefore == _forgotten)
            {
                _found = (ip, timeProvider.GetUtcNow());
            }
        }
        return ip;
    }

    /// <summary>
    /// The IP address that is the whole of the page's text, if it is one. An IPv4 address must have all four parts,
    /// as <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> reads an error page's "429" as one.
    /// </summary>
    private static string? IpIn(string? text)
    {
        text = text?.Trim();
        if (!IPAddress.TryParse(text, out var ip))
        {
            return null;
        }
        var whole = ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => text.Split('.') is { Length: 4 } parts && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)),
            AddressFamily.InterNetworkV6 => text.Contains(':', StringComparison.Ordinal),
            _ => false
        };
        return whole ? ip.ToString() : null;
    }
}
