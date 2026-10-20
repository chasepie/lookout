using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public static class SignedInClients
{
    public const string UserName = "owner";
    public const string Password = "correct horse";

    /// <summary>Creates a client that has set up the host's account and holds its session cookie.</summary>
    public static async Task<HttpClient> CreateSignedInClientAsync(this WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken, WebApplicationFactoryClientOptions? options = null)
    {
        var client = factory.CreateClient(options ?? new WebApplicationFactoryClientOptions());
        try
        {
            var setup = await client.PostAsJsonAsync("/api/setup", new { userName = UserName, password = Password }, cancellationToken);
            setup.EnsureSuccessStatusCode();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
