using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// The VPN's region, its rotation settings, and switching region on demand.
/// Every endpoint but <see cref="Get"/> fails with 409 when the VPN isn't set up, and with 503 when gluetun
/// can't be reached.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VpnController(
    LookoutDbContext dbContext,
    VpnConfiguration configuration,
    VpnRegionCatalog catalog,
    VpnRotationService rotationService,
    GluetunHttpClient gluetun,
    UpdatesNotifier notifier) : ControllerBase
{
    private const string NotConfiguredMessage =
        $"The VPN isn't set up: the Backend has no gluetun control server URL ({EnvironmentVariables.GluetunUrl}).";

    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly VpnConfiguration _configuration = configuration;
    private readonly VpnRegionCatalog _catalog = catalog;
    private readonly VpnRotationService _rotationService = rotationService;
    private readonly GluetunHttpClient _gluetun = gluetun;
    private readonly UpdatesNotifier _notifier = notifier;

    /// <summary>
    /// The settings, the switch in progress, and the tunnel's public IP as gluetun reports it.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<VpnStatus>> Get(CancellationToken cancellationToken)
    {
        if (!_configuration.IsEnabled)
        {
            return new VpnStatus(false, null, null, null, null);
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        GluetunPublicIp? publicIp = null;
        string? gluetunError = null;
        try
        {
            publicIp = await _gluetun.GetPublicIpAsync(cancellationToken);
        }
        catch (GluetunException ex)
        {
            gluetunError = ex.Message;
        }

        return new VpnStatus(true, settings, _rotationService.Progress, publicIp, gluetunError);
    }

    /// <summary>
    /// The regions the VPN provider offers, from gluetun's servers list.
    /// </summary>
    [HttpGet("regions")]
    public async Task<ActionResult<VpnRegions>> GetRegions(CancellationToken cancellationToken)
    {
        if (!_configuration.IsEnabled)
        {
            return Conflict(NotConfiguredMessage);
        }

        try
        {
            return await _catalog.GetRegionsAsync(_gluetun, cancellationToken);
        }
        catch (GluetunException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    [HttpPut("settings")]
    public async Task<ActionResult<VpnSettings>> UpdateSettings(VpnSettingsDto dto, CancellationToken cancellationToken)
    {
        if (!_configuration.IsEnabled)
        {
            return Conflict(NotConfiguredMessage);
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        settings.Update(dto, DateTime.UtcNow, Random.Shared);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _notifier.VpnChangedAsync();
        return settings;
    }

    /// <summary>
    /// Switches region soon, restarting the rotation timer. Returns before the switch happens: it waits for
    /// a VPN run in progress, and clients hear how it went through the updates hub's <c>VpnChanged</c>.
    /// </summary>
    [HttpPost("switch")]
    public async Task<IActionResult> Switch(VpnSwitchRequest request, CancellationToken cancellationToken)
    {
        if (!_configuration.IsEnabled)
        {
            return Conflict(NotConfiguredMessage);
        }

        string? region = null;
        if (!string.IsNullOrWhiteSpace(request.Region))
        {
            VpnRegions regions;
            try
            {
                regions = await _catalog.GetRegionsAsync(_gluetun, cancellationToken);
            }
            catch (GluetunException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
            }

            // Matches gluetun's case-insensitive check, but sends the list's spelling, which reapplying compares to
            region = regions.Regions
                .FirstOrDefault(r => string.Equals(r, request.Region.Trim(), StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return BadRequest($"{request.Region} isn't one of the VPN provider's regions.");
            }
        }

        _rotationService.RequestSwitch(region);
        return Accepted();
    }

    private Task<VpnSettings> LoadSettingsAsync(CancellationToken cancellationToken) =>
        _dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
}

/// <param name="Configured">Whether the Backend knows where gluetun is. If not, every other field is null.</param>
/// <param name="Switch">The region switch in progress, if any.</param>
/// <param name="PublicIp">The tunnel's public IP, or null while the tunnel is down or gluetun is unreachable.</param>
/// <param name="GluetunError">Why gluetun couldn't be reached, if it couldn't.</param>
public sealed record VpnStatus(
    bool Configured,
    VpnSettings? Settings,
    VpnSwitchProgress? Switch,
    GluetunPublicIp? PublicIp,
    string? GluetunError);

/// <param name="Region">The region to switch to, or null for a random one that isn't the current region.</param>
public sealed record VpnSwitchRequest(string? Region);
