using System.Net;
using System.Net.Sockets;

namespace ChasePie.Lookout.Backend.Security;

/// <summary>
/// Limits failed logins, so that guessing the Account's password is impractical (ADR-0002): after
/// <see cref="MaximumFailures"/> failed logins from a client address within <see cref="Window"/>, its logins are
/// refused, without being checked, until that window ends. A login that succeeds starts the address's count again.
/// </summary>
/// <remarks>
/// The address is the real peer, as Lookout doesn't trust <c>X-Forwarded-For</c>, so behind a reverse proxy every
/// client shares the proxy's limit. An IPv6 client is counted by its /64 network, as it can choose any address in it.
/// Counts are kept in memory, and start again on restart.
/// </remarks>
public sealed class LoginLimit(TimeProvider timeProvider)
{
    public const int MaximumFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Failures> _failures = [];

    /// <summary>
    /// Starts a login attempt from the address, counting it as failed until <see cref="Succeeded"/> says otherwise,
    /// so that attempts made at once can't get past the limit. Returns how long to wait instead, if the address has
    /// reached it.
    /// </summary>
    public TimeSpan? StartAttempt(IPAddress? address)
    {
        var client = ClientKey(address);
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_failures.TryGetValue(client, out var failures) || now >= failures.WindowEnd)
            {
                RemoveEnded(now);
                _failures[client] = new Failures(now + Window, 1);
                return null;
            }

            if (failures.Count >= MaximumFailures)
            {
                return failures.WindowEnd - now;
            }

            _failures[client] = failures with { Count = failures.Count + 1 };
            return null;
        }
    }

    public void Succeeded(IPAddress? address)
    {
        lock (_lock)
        {
            _failures.Remove(ClientKey(address));
        }
    }

    private void RemoveEnded(DateTimeOffset now)
    {
        foreach (var (client, failures) in _failures)
        {
            if (now >= failures.WindowEnd)
            {
                _failures.Remove(client);
            }
        }
    }

    private static IPAddress ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return IPAddress.None;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var network = address.GetAddressBytes();
        Array.Clear(network, 8, 8);
        return new IPAddress(network);
    }

    private sealed record Failures(DateTimeOffset WindowEnd, int Count);
}
