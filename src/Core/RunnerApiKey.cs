using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// The <c>LOOKOUT_RUNNER_API_KEY</c> secret the Backend sends the Runner in the <see cref="HeaderName"/> header, and
/// without which the Runner refuses requests. Resolve it once at startup with <see cref="FromConfiguration"/>.
/// </summary>
/// <remarks>Why the Runner needs it, and why it isn't enough on its own: <c>docs/security.md</c>.</remarks>
public sealed class RunnerApiKey
{
    public const string HeaderName = "X-API-Key";

    public const int MinLength = LookoutEnvironmentVariables.RunnerApiKeyMinLength;

    private readonly byte[] _hash;

    /// <exception cref="ArgumentException"><paramref name="value"/> is shorter than <see cref="MinLength"/>.</exception>
    public RunnerApiKey(string value)
    {
        if (value is null || value.Length < MinLength)
        {
            throw new ArgumentException($"The Runner's API key must be at least {MinLength} characters.", nameof(value));
        }

        Value = value;
        _hash = Hash(value);
    }

    public string Value { get; }

    /// <summary>
    /// Reads the key from configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">The variable is missing, or the key is too short.</exception>
    public static RunnerApiKey FromConfiguration(IConfiguration configuration)
    {
        var value = configuration[LookoutEnvironmentVariables.RunnerApiKey];
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinLength)
        {
            throw new InvalidOperationException(
                $"Environment variable {LookoutEnvironmentVariables.RunnerApiKey} must be set to a key of at least "
                + $"{MinLength} characters. The AppHost generates one; outside it, set the same key for the Backend "
                + "and the Runner.");
        }

        return new RunnerApiKey(value);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the key. Runs in constant time, revealing neither how much of it
    /// matched nor its length.
    /// </summary>
    public bool Matches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Hash(candidate), _hash);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
