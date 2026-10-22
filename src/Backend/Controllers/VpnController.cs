using ChasePie.Lookout.Backend.Vpn;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>The VPN that VPN Runs go through, for the Settings screen.</summary>
[ApiController]
[Route("api/vpn")]
public sealed class VpnController(VpnRotation rotation) : LookoutControllerBase
{
    [HttpGet]
    public Task<VpnStatus> Status(CancellationToken cancellationToken) => rotation.GetStatusAsync(cancellationToken);
}
