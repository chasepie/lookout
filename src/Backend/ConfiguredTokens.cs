namespace ChasePie.Lookout.Backend;

/// <summary>Reads the tokens and keys Lookout is configured with to authenticate to the services it calls.</summary>
public static class ConfiguredTokens
{
    /// <summary>
    /// The token configured as <paramref name="key"/>, or null when it's unset or empty. Throws unless it holds only
    /// letters, digits, '-' and '_'. A random token needs nothing else, and so it can't change the header it's sent in,
    /// or the TOML file deployment writes the VPN's key into.
    /// </summary>
    public static string? Read(IConfiguration configuration, string key)
    {
        var token = configuration[key];
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        if (!token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InvalidOperationException($"{key} must contain only letters, digits, '-' and '_', such as from openssl rand -hex 32");
        }
        return token;
    }
}
