using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// Settings read from files, so secrets can stay out of the services' environments, where <c>docker inspect</c> and
/// every child process could see them. See <see cref="LookoutEnvironmentVariables.SecretsDir"/>.
/// </summary>
public static class SecretFiles
{
    public const string DefaultDirectory = "/run/secrets";

    /// <summary>
    /// Adds the files in <see cref="LookoutEnvironmentVariables.SecretsDir"/>, or <see cref="DefaultDirectory"/>, as
    /// settings that win over the ones already added. Each file is one setting, named after it; a trailing newline is
    /// dropped. Without the directory, nothing is added.
    /// </summary>
    public static IConfigurationManager AddSecretFiles(this IConfigurationManager configuration)
    {
        var directory = configuration[LookoutEnvironmentVariables.SecretsDir] is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : DefaultDirectory;
        configuration.AddKeyPerFile(directory, optional: true, reloadOnChange: false);
        return configuration;
    }
}
