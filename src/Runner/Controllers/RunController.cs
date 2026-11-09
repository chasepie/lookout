using ChasePie.Lookout.Core;
using ChasePie.Lookout.Runner.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Runner.Controllers;

[ApiController]
[Route("[controller]")]
public class WatchRunController(ScriptRunner scriptRunner) : ControllerBase
{
    /// <summary>
    /// Runs the named script and returns its result. Aborting the request stops the script.
    /// </summary>
    /// <param name="scriptName">The script's file name in the watch scripts directory.</param>
    /// <param name="useVpn">Whether the browser's traffic goes through the VPN proxy.</param>
    /// <param name="screenshotOnSuccess">See <see cref="ScreenshotSettings.OnSuccess"/>.</param>
    /// <param name="screenshotOnKnownFailure">See <see cref="ScreenshotSettings.OnKnownFailure"/>.</param>
    /// <param name="screenshotOnCrash">See <see cref="ScreenshotSettings.OnCrash"/>.</param>
    /// <param name="cancellationToken">Cancelled when the request is aborted.</param>
    [HttpGet(Name = "GetWatchRunResult")]
    public async Task<ActionResult<WatchRunResult>> GetAsync(
        [FromQuery] string scriptName,
        CancellationToken cancellationToken,
        [FromQuery] bool useVpn = false,
        [FromQuery] bool screenshotOnSuccess = false,
        [FromQuery] bool screenshotOnKnownFailure = false,
        [FromQuery] bool screenshotOnCrash = false)
    {
        try
        {
            var screenshots = new ScreenshotSettings(screenshotOnSuccess, screenshotOnKnownFailure, screenshotOnCrash);
            return await scriptRunner.RunWatchScriptAsync(scriptName, useVpn, screenshots, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The Backend records this message on the run
            return Problem(ex.Message);
        }
    }
}
