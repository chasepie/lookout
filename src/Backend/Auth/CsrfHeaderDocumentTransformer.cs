using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ChasePie.Lookout.Backend.Auth;

/// <summary>
/// Documents the header <see cref="CsrfProtection"/> requires as a security scheme, so API clients such as Scalar send
/// it with every request.
/// </summary>
public sealed class CsrfHeaderDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "CsrfHeader";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = CsrfProtection.HeaderName,
            Description = "Required on requests that change data, with any value, as protection against cross-site "
                + "request forgery. Requests also need the session cookie, which signing in sets.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, document)] = [],
        });
        return Task.CompletedTask;
    }
}
