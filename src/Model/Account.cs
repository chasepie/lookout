namespace ChasePie.Lookout.Model;

/// <summary>The single local account that protects Lookout (ADR-0002).</summary>
public sealed class Account
{
    /// <summary>The only Id an Account can have, so the database refuses a second Account.</summary>
    public const int SingletonId = 1;
    public const int MinimumUserNameLength = 1;
    public const int MaximumUserNameLength = 100;
    public const int MinimumPasswordLength = 8;
    public const int MaximumPasswordLength = 1024;

    public int Id { get; set; } = SingletonId;
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>Whether <paramref name="userName"/>, once trimmed, can be the Account's user name.</summary>
    public static bool IsValidUserName(string userName) =>
        userName.Trim().Length is >= MinimumUserNameLength and <= MaximumUserNameLength;

    /// <summary>Whether <paramref name="password"/> can be the Account's password.</summary>
    public static bool IsValidPassword(string password) =>
        password.Length is >= MinimumPasswordLength and <= MaximumPasswordLength;
}
