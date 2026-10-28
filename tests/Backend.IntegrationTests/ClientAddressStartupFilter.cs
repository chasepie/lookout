using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Gives a test request the client address in its <see cref="HeaderName"/> header, as the test server's requests
/// otherwise all come from no address at all.
/// </summary>
public sealed class ClientAddressStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var header)
                && IPAddress.TryParse(header.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }
            return nextMiddleware(context);
        });
        next(app);
    };
}
