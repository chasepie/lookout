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
    [HttpGet(Name = "GetWatchRunResult")]
    public async Task<ActionResult<WatchRunResult>> GetAsync(
        [FromQuery] string scriptName, CancellationToken cancellationToken)
    {
        try
        {
            return await scriptRunner.RunWatchScriptAsync(scriptName, cancellationToken);
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
