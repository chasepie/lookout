using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Net.Http.Headers;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed record WatchEvent(string Name, JsonElement Payload);

/// <summary>
/// A browser tab of its own listening to Lookout's events: it has its own cookies, so its own login (or none), and
/// connects to the events hub over WebSockets with the request token, as the Angular app does.
/// </summary>
public sealed class WatchEventsListener : IAsyncDisposable
{
    private static readonly string[] EventNames =
    [
        nameof(IWatchEventsClient.RunStarted), nameof(IWatchEventsClient.RunFinished),
        nameof(IWatchEventsClient.WatchStatusChanged)
    ];
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly CookieContainer _cookies = new();
    private readonly Channel<WatchEvent> _events = Channel.CreateUnbounded<WatchEvent>();
    /// <summary>The VPN's events, kept apart as a Rotation can push them at any time fake time moves on.</summary>
    private readonly Channel<JsonElement> _vpnEvents = Channel.CreateUnbounded<JsonElement>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HubConnection? _connection;

    private WatchEventsListener(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        Client = factory.CreateDefaultClient(new CookieContainerHandler(_cookies));
    }

    /// <summary>A client holding this listener's cookies, such as for logging its Session out.</summary>
    public HttpClient Client { get; }

    /// <summary>Completes when the hub closes the connection.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Logs in to the account an earlier client set up, then connects to the events hub.</summary>
    public static async Task<WatchEventsListener> ConnectAsync(WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken)
    {
        var listener = await CreateAsync(factory, logIn: true, cancellationToken);
        try
        {
            await listener.StartAsync(cancellationToken: cancellationToken);
            return listener;
        }
        catch
        {
            await listener.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates a listener that has its request token and, if asked, a login, but hasn't connected yet.</summary>
    public static async Task<WatchEventsListener> CreateAsync(WebApplicationFactory<Program> factory, bool logIn,
        CancellationToken cancellationToken)
    {
        var listener = new WatchEventsListener(factory);
        try
        {
            await listener.Client.RefreshRequestTokenAsync(cancellationToken);
            if (logIn)
            {
                var login = await listener.Client.PostAsJsonAsync("/api/session",
                    new { userName = SignedInClients.UserName, password = SignedInClients.Password }, cancellationToken);
                login.EnsureSuccessStatusCode();
                await listener.Client.RefreshRequestTokenAsync(cancellationToken);
            }
            return listener;
        }
        catch
        {
            await listener.DisposeAsync();
            throw;
        }
    }

    /// <summary>Connects to the events hub, by default the way the app does.</summary>
    public async Task StartAsync(bool sendRequestToken = true, bool skipNegotiation = false, string query = "",
        CancellationToken cancellationToken = default)
    {
        var server = _factory.Server;
        var cookieHeader = _cookies.GetCookieHeader(server.BaseAddress);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, WatchEventsHub.Path + query), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = skipNegotiation;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Headers[HeaderNames.Cookie] = cookieHeader;
                if (sendRequestToken)
                {
                    options.Headers[SignedInClients.RequestTokenHeader] =
                        _cookies.GetAllCookies().Single(cookie => cookie.Name == RequestForgery.TokenCookieName).Value;
                }
                options.WebSocketFactory = async (context, token) =>
                {
                    // A browser sends cookies, but not headers, when it opens a WebSocket.
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Cookie = cookieHeader;
                    return await client.ConnectAsync(context.Uri, token);
                };
            })
            .Build();
        foreach (var name in EventNames)
        {
            connection.On<JsonElement>(name, payload => _events.Writer.TryWrite(new WatchEvent(name, payload)));
        }
        connection.On<JsonElement>(nameof(IWatchEventsClient.VpnStatusChanged), payload => _vpnEvents.Writer.TryWrite(payload));
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
        _connection = connection;
        await connection.StartAsync(cancellationToken);
    }

    /// <summary>The next events pushed to this listener, in the order they arrived.</summary>
    public async Task<IReadOnlyList<WatchEvent>> NextAsync(int count, CancellationToken cancellationToken)
    {
        var events = new List<WatchEvent>();
        for (var index = 0; index < count; index++)
        {
            events.Add(await _events.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(EventTimeout, cancellationToken));
        }
        return events;
    }

    public async Task<WatchEvent> NextAsync(CancellationToken cancellationToken) => (await NextAsync(1, cancellationToken))[0];

    /// <summary>The next VPN status pushed to this listener.</summary>
    public async Task<JsonElement> NextVpnStatusAsync(CancellationToken cancellationToken) =>
        await _vpnEvents.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(EventTimeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        Client.Dispose();
    }
}
