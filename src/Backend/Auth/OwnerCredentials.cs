using System.Security.Cryptography;
using System.Text;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// The Owner's username and password, from <c>LOOKOUT_OWNER_USERNAME</c> and <c>LOOKOUT_OWNER_PASSWORD</c>: the only
/// credentials Lookout accepts. See <c>docs/security.md</c>.
/// </summary>
public sealed class OwnerCredentials
{
    public const int MinPasswordLength = EnvironmentVariables.OwnerPasswordMinLength;

    // Iterations for deriving the fingerprint. It's derived once per start, so it can afford to be slow.
    private const int FingerprintIterations = 100_000;

    private readonly byte[] _usernameHash;
    private readonly byte[] _passwordHash;

    /// <exception cref="ArgumentException">The username is empty, or the password is shorter than
    /// <see cref="MinPasswordLength"/>.</exception>
    public OwnerCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("The Owner's username must not be empty.", nameof(username));
        }

        if (password is null || password.Length < MinPasswordLength)
        {
            throw new ArgumentException(
                $"The Owner's password must be at least {MinPasswordLength} characters.", nameof(password));
        }

        Username = username;
        _usernameHash = HashUsername(username);
        _passwordHash = Hash(password);
        Fingerprint = DeriveFingerprint(username, password);
    }

    public string Username { get; }

    /// <summary>
    /// Changes whenever the username or password changes, without revealing either. Sessions carry it, so changing the
    /// credentials signs them all out.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Reads the credentials from configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">A variable is missing, or the password is too short.</exception>
    public static OwnerCredentials FromConfiguration(IConfiguration configuration)
    {
        var username = configuration[EnvironmentVariables.LookoutOwnerUsername];
        var password = configuration[EnvironmentVariables.LookoutOwnerPassword];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"Environment variables {EnvironmentVariables.LookoutOwnerUsername} and "
                + $"{EnvironmentVariables.LookoutOwnerPassword} must be set to the Owner's sign-in.");
        }

        try
        {
            return new OwnerCredentials(username, password);
        }
        catch (ArgumentException ex) when (ex.ParamName == nameof(password))
        {
            // The constructor holds the rules; this only names the variable to fix
            throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariables.LookoutOwnerPassword} must be at least "
                + $"{MinPasswordLength} characters.", ex);
        }
    }

    /// <summary>
    /// Whether the given credentials are the Owner's. The username is compared ignoring case.
    /// </summary>
    public bool Matches(string username, string password)
    {
        // Constant time, and both always run: the response time mustn't reveal which part was wrong
        var usernameMatches = CryptographicOperations.FixedTimeEquals(HashUsername(username), _usernameHash);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(Hash(password), _passwordHash);
        return usernameMatches & passwordMatches;
    }

    private static byte[] HashUsername(string username) => Hash(username.ToUpperInvariant());

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string DeriveFingerprint(string username, string password)
    {
        // Salted with the username, so changing either credential changes the fingerprint
        var salt = Hash("lookout-owner-session:" + username.ToUpperInvariant());
        var fingerprint = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, FingerprintIterations, HashAlgorithmName.SHA256, outputLength: 32);
        return Convert.ToBase64String(fingerprint);
    }
}
