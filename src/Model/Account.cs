namespace ChasePie.Lookout.Model;

/// <summary>The single local account that protects Lookout (ADR-0002).</summary>
public sealed class Account
{
    /// <summary>The only Id an Account can have, so the database refuses a second Account.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }
}
