namespace ChasePie.Lookout.Backend.Controllers;

public sealed record DebugOutputSummary(string? ConsoleOutput, bool HasScreenshot);

public sealed class TestRunRequest
{
    public required IFormFile Script { get; init; }
}
