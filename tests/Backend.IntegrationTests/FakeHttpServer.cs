using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed record ReceivedRequest(string Method, string PathAndQuery, string? ContentType, string Body);

/// <summary>A real HTTP server on a free local port that records each request and answers as the test says.</summary>
public sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeHttpServer(WebApplication app) => _app = app;

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();
    /// <summary>Answers each request; by default with 204 No Content.</summary>
    public Func<HttpContext, Task> Respond { get; set; } = context =>
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    };
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The request <see cref="Respond"/> is answering, as recorded, since its body has already been read.</summary>
    public static ReceivedRequest RequestOf(HttpContext context) => (ReceivedRequest)context.Items[typeof(ReceivedRequest)]!;

    public static async Task<FakeHttpServer> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new FakeHttpServer(app);
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var received = new ReceivedRequest(context.Request.Method,
                context.Request.Path + context.Request.QueryString, context.Request.ContentType, body);
            server.Requests.Enqueue(received);
            context.Items[typeof(ReceivedRequest)] = received;
            await server.Respond(context);
        });
        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.BaseAddress = new Uri(address + "/");
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
