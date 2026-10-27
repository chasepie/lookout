using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Startup;

/// <summary>
/// The Account as <c>Account:UserName</c> and <c>Account:Password</c> configure it. Set on every start, before Lookout
/// accepts requests, so first-launch setup is never open, and changing them changes or recovers the password.
/// </summary>
public sealed partial class ConfiguredAccount
{
    internal const string AccountUserName = "Account:UserName";
    internal const string AccountPassword = "Account:Password";

    private ConfiguredAccount(string userName, string password)
    {
        UserName = userName;
        Password = password;
    }

    /// <summary>The user name, trimmed.</summary>
    public string UserName { get; }
    public string Password { get; }

    /// <summary>
    /// The Account the settings configure, or null without either setting, which leaves setup and the Account to the
    /// app. Throws for only one setting or a value setup would refuse, with a message that names the setting but never
    /// repeats its value. Read before the database is migrated, so a refused setting changes nothing.
    /// </summary>
    public static ConfiguredAccount? Read(IConfiguration configuration)
    {
        // An empty setting is unset, as compose passes an unset variable.
        var userName = configuration[AccountUserName];
        var password = configuration[AccountPassword];
        if (string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password))
        {
            return null;
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"{AccountPassword} must be set with {AccountUserName}, or neither set to set up the Account in the app");
        }

        if (string.IsNullOrEmpty(userName))
        {
            throw new InvalidOperationException(
                $"{AccountUserName} must be set with {AccountPassword}, or neither set to set up the Account in the app");
        }

        if (!Account.IsValidUserName(userName))
        {
            throw new InvalidOperationException(
                $"{AccountUserName} must contain between {Account.MinimumUserNameLength} and {Account.MaximumUserNameLength} characters once trimmed");
        }

        if (!Account.IsValidPassword(password))
        {
            throw new InvalidOperationException(
                $"{AccountPassword} must contain between {Account.MinimumPasswordLength} and {Account.MaximumPasswordLength} characters");
        }

        return new ConfiguredAccount(userName.Trim(), password);
    }

    /// <summary>
    /// Creates the Account from the settings, or makes the existing one match them, ending its Sessions if its user
    /// name or password changes. Does nothing without <paramref name="configured"/>.
    /// </summary>
    public static async Task ApplyAsync(WebApplication app, ConfiguredAccount? configured)
    {
        if (configured is null)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();

        await using var transaction = await database.Database.BeginTransactionAsync();
        var account = await database.Accounts.SingleOrDefaultAsync();
        if (account is null)
        {
            account = new Account
            {
                UserName = configured.UserName,
                PasswordHash = ""
            };
            account.PasswordHash = passwordHasher.HashPassword(account, configured.Password);
            database.Accounts.Add(account);
            LogCreated(app.Logger);
        }
        else
        {
            // Even a change of case is a new user name, which each Session's cookie would still show as it was.
            var changed = account.UserName != configured.UserName;
            account.UserName = configured.UserName;
            // A hash in an older format is left for login to upgrade, so the same settings change nothing.
            if (passwordHasher.VerifyHashedPassword(account, account.PasswordHash, configured.Password) == PasswordVerificationResult.Failed)
            {
                account.PasswordHash = passwordHasher.HashPassword(account, configured.Password);
                changed = true;
            }
            if (changed)
            {
                // Whoever knew the old user name or password mustn't stay logged in with it.
                await database.Sessions.ExecuteDeleteAsync();
                LogChanged(app.Logger);
            }
        }

        await database.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    [LoggerMessage(LogLevel.Information, $"Created the Account from {AccountUserName} and {AccountPassword}.")]
    private static partial void LogCreated(ILogger logger);

    [LoggerMessage(LogLevel.Information, $"Changed the Account to match {AccountUserName} and {AccountPassword}, ending every Session.")]
    private static partial void LogChanged(ILogger logger);
}
