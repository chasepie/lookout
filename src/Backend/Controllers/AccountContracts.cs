namespace ChasePie.Lookout.Backend.Controllers;

public sealed record SessionStatus(bool AccountExists, string? UserName);

public sealed record Credentials(string UserName, string Password);
