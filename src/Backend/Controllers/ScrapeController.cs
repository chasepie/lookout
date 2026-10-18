using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace ChasePie.Lookout.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ScrapeController : ControllerBase
{
    // post: api/scrape
    [HttpPost]
    public async Task<IActionResult> ScrapeAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Channel = "chrome",
            Args = ["--disable-blink-features=AutomationControlled"],
            ChromiumSandbox = true
        });
        var page = await browser.NewPageAsync();
        await page.GotoAsync("https://www.browserscan.net/bot-detection");

        page.Close += async (sender, e) =>
        {
            await page.DisposeAsync();
            await browser.CloseAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
        };

        return Ok("Scrape endpoint hit");
    }
}
