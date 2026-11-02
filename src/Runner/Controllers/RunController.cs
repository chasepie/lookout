using ChasePie.Lookout.Runner.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Runner.Controllers;

[ApiController]
[Route("[controller]")]
public class WatchRunController(ScriptRunner scriptRunner) : ControllerBase
{
    [HttpGet(Name = "GetWatchRunResult")]
    public async Task<WatchRunResult> GetAsync([FromQuery] string scriptName)
    {
        return await scriptRunner.RunWatchScriptAsync(scriptName);
    }
}
