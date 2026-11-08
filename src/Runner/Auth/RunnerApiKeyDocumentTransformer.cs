using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ChasePie.Lookout.Runner.Auth;

/// <summary>
/// Documents the header <see cref="RunnerApiKeyAuthentication"/> requires as a security scheme, so API clients such as
/// Scalar can send it.
/// </summary>
public sealed class RunnerApiKeyDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string OpenApiSchemeName = "RunnerApiKey";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[OpenApiSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = RunnerApiKey.HeaderName,
            Description = $"Required on every request: the value of {EnvironmentVariables.LookoutRunnerApiKey}. "
                + "The AppHost generates a new one on each start; the Aspire dashboard shows it under the "
                + "runner-api-key parameter.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(OpenApiSchemeName, document)] = [],
        });
        return Task.CompletedTask;
    }
}
