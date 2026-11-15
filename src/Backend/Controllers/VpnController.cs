using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// The VPN's region, its rotation settings, and switching region on demand, as <see cref="Vpn"/> has them.
/// Every endpoint but <see cref="Get"/> fails with 409 when the VPN isn't set up, and with 503 when gluetun
/// can't be reached.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VpnController(Vpn vpn) : ControllerBase
{
    private const string NotConfiguredMessage =
        $"The VPN isn't set up: the Backend has no gluetun control server URL ({EnvVars.GluetunUrl}).";

    private readonly Vpn _vpn = vpn;

    /// <summary>
    /// The settings, the switch in progress, and the tunnel's public IP as gluetun reports it.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<VpnStatus>> Get(CancellationToken cancellationToken) =>
        await _vpn.GetStatusAsync(cancellationToken);

    /// <summary>
    /// The regions the VPN provider offers, from gluetun's servers list, limited to <c>GLUETUN_SERVER_REGIONS</c>.
    /// </summary>
    [HttpGet("regions")]
    public async Task<ActionResult<VpnRegions>> GetRegions(CancellationToken cancellationToken)
    {
        try
        {
            return await _vpn.GetRegionsAsync(cancellationToken) is { } regions
                ? regions
                : Conflict(NotConfiguredMessage);
        }
        catch (GluetunException ex)
        {
            return GluetunUnreachable(ex);
        }
    }

    [HttpPut("settings")]
    public async Task<ActionResult<VpnSettings>> UpdateSettings(VpnSettingsDto dto, CancellationToken cancellationToken) =>
        await _vpn.UpdateSettingsAsync(dto, cancellationToken) is { } settings
            ? settings
            : Conflict(NotConfiguredMessage);

    /// <summary>
    /// Switches region soon, restarting the rotation timer. Returns before the switch happens: it waits for
    /// a VPN run in progress, and clients hear how it went through the updates hub's <c>VpnChanged</c>.
    /// </summary>
    [HttpPost("switch")]
    public async Task<IActionResult> Switch(VpnSwitchRequest request, CancellationToken cancellationToken)
    {
        ManualSwitchResult result;
        try
        {
            result = await _vpn.RequestSwitchAsync(request.Region, cancellationToken);
        }
        catch (GluetunException ex)
        {
            return GluetunUnreachable(ex);
        }

        return result switch
        {
            ManualSwitchResult.NotSetUp => Conflict(NotConfiguredMessage),
            ManualSwitchResult.UnknownRegion => BadRequest(
                $"{request.Region} isn't one of the VPN provider's regions, or isn't in {EnvVars.GluetunServerRegions}."),
            _ => Accepted(),
        };
    }

    private ObjectResult GluetunUnreachable(GluetunException ex) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
}

/// <param name="Region">The region to switch to, or null for a random one that isn't the current region.</param>
public sealed record VpnSwitchRequest(string? Region);
