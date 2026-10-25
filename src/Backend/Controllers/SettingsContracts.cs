namespace ChasePie.Lookout.Backend.Controllers;

public sealed record SettingsResponse(int RetentionDays);

public sealed class SettingsRequest
{
    public required int RetentionDays { get; init; }
}
