using System.Text.Json.Serialization;
using ChasePie.Lookout.Backend.Runner;

namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>The VPN's state. Only a connected VPN has a proxy, so a VPN Run can't go through one that isn't.</summary>
public sealed record VpnStatus
{
    /// <summary>Whether Lookout is configured with a VPN at all.</summary>
    public bool Enabled { get; private init; }
    /// <summary>Whether VPN Runs can go through it now.</summary>
    public bool Connected => Proxy is not null;
    /// <summary>Whether Lookout is rotating the VPN to a new exit IP, during which no VPN Run starts.</summary>
    public bool Rotating { get; private init; }
    public string? Region { get; private init; }
    public string? ExitIp { get; private init; }
    /// <summary>Why the VPN isn't connected, if that's known.</summary>
    public string? Error { get; private init; }
    /// <summary>The proxy a VPN Run's browser goes through. Left out of the API, as it may carry a password.</summary>
    [JsonIgnore]
    public ProxySettings? Proxy { get; private init; }
    /// <summary>Whether the VPN can't connect until its setup is fixed, such as a key gluetun refuses, so waiting is no use.</summary>
    [JsonIgnore]
    public bool Misconfigured { get; private init; }
    /// <summary>Whether Lookout reached the VPN to read this, rather than it not answering, such as before it starts.</summary>
    [JsonIgnore]
    public bool Reached { get; private init; }

    public static readonly VpnStatus Disabled = new();

    public static VpnStatus Disconnected(string? error = null) => new() { Enabled = true, Error = error, Reached = true };

    /// <summary>The VPN didn't answer, so whether it's connecting, or even started, isn't known.</summary>
    public static VpnStatus Unreachable(string error) => new() { Enabled = true, Error = error };

    public static VpnStatus MisconfiguredBecause(string error) =>
        new() { Enabled = true, Error = error, Misconfigured = true, Reached = true };

    public static VpnStatus ConnectedThrough(ProxySettings proxy, string exitIp, string? region) =>
        new() { Enabled = true, Proxy = proxy, ExitIp = exitIp, Region = region, Reached = true };

    /// <summary>This status, during a Rotation.</summary>
    public VpnStatus WhileRotating() => this with { Rotating = true };

    /// <summary>
    /// Why <paramref name="run"/>, such as a VPN Run, can't go ahead, or nothing when it can go through the VPN. It
    /// fails closed: without the VPN it would go out from the home IP, or hang until its timeout.
    /// </summary>
    public string? RefusalOf(string run) => Connected
        ? null
        : $"{(Enabled ? "The VPN is not connected" : "No VPN is configured")}, so this {run} was not started. {Error}".TrimEnd();
}
