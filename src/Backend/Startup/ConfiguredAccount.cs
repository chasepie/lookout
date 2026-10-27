using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Startup;

/// <summary>
/// The Account as <c>Account:UserName</c> and <c>Account:Password</c> configure it. Set on every start, before Lookout
/// accepts requests, so first-launch setup is never open, and changing them changes or recovers the password.
/// </summary>
public static class ConfiguredAccount
{
    /// <summary>
    /// Creates the Account from the settings, or makes the existing one match them, ending its Sessions if its user
    /// name or password changes. Without either setting, setup and the Account are left to the app. Throws, changing
    /// nothing, for only one setting or a value setup would refuse, with a message that names the setting but never
    /// repeats its value.
    /// </summary>
    public static async Task ApplyAsync(WebApplication app)
    {
        // An empty setting is unset, as compose passes an unset variable.
        var userName = app.Configuration["Account:UserName"];
        var password = app.Configuration["Account:Password"];
        if (string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password))
        {
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Account:Password must be set with Account:UserName, or neither set to set up the Account in the app");
        }
        if (string.IsNullOrEmpty(userName))
        {
            throw new InvalidOperationException(
                "Account:UserName must be set with Account:Password, or neither set to set up the Account in the app");
        }
        if (!Account.IsValidUserName(userName))
        {
            throw new InvalidOperationException(
                $"Account:UserName must contain between {Account.MinimumUserNameLength} and {Account.MaximumUserNameLength} characters once trimmed");
        }
        if (!Account.IsValidPassword(password))
        {
            throw new InvalidOperationException(
                $"Account:Password must contain between {Account.MinimumPasswordLength} and {Account.MaximumPasswordLength} characters");
        }

        userName = userName.Trim();
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();
        await using var transaction = await database.Database.BeginTransactionAsync();
        var account = await database.Accounts.SingleOrDefaultAsync();
        if (account is null)
        {
            account = new Account { UserName = userName, PasswordHash = "" };
            account.PasswordHash = passwordHasher.HashPassword(account, password);
            database.Accounts.Add(account);
        }
        else
        {
            // Even a change of case is a new user name, which each Session's cookie would still show as it was.
            var changed = account.UserName != userName;
            account.UserName = userName;
            if (passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password) == PasswordVerificationResult.Failed)
            {
                account.PasswordHash = passwordHasher.HashPassword(account, password);
                changed = true;
            }
            if (changed)
            {
                // Whoever knew the old user name or password mustn't stay logged in with it.
                await database.Sessions.ExecuteDeleteAsync();
            }
        }
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
