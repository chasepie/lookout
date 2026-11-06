using System.Net;
using System.Net.Sockets;
using System.Text;
using ChasePie.Lookout.Backend.VpnServices;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class VpnProbeHttpClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SendsTheCredentialsInTheProxyUrl()
    {
        using var proxy = new FakeProxy();
        await using var provider = CreateServices(new Uri($"http://lookout:p%40ss@127.0.0.1:{proxy.Port}/"));

        await provider.GetRequiredService<VpnProbeHttpClient>().TryGetPublicIpAsync(TestContext.CancellationToken);

        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("lookout:p@ss")), proxy.Authorization);
    }

    [TestMethod]
    public async Task SendsNoCredentialsWhenTheProxyUrlHasNone()
    {
        using var proxy = new FakeProxy();
        await using var provider = CreateServices(new Uri($"http://127.0.0.1:{proxy.Port}/"));

        await provider.GetRequiredService<VpnProbeHttpClient>().TryGetPublicIpAsync(TestContext.CancellationToken);

        Assert.IsNull(proxy.Authorization);
    }

    private static ServiceProvider CreateServices(Uri proxyUrl)
    {
        var services = new ServiceCollection()
            .AddSingleton(new VpnConfiguration(new Uri("http://gluetun.test/"), null, null, proxyUrl));
        services.AddHttpClient<VpnProbeHttpClient>().ConfigureVpnProbeHttpClient();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// An HTTP proxy that, like gluetun's with credentials set, answers 407 until a request brings credentials, then
    /// records them and refuses the request.
    /// </summary>
    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeProxy()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>
        /// The <c>Proxy-Authorization</c> header of the last request that had one.
        /// </summary>
        public string? Authorization { get; private set; }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 })
                {
                    string? authorization = null;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                    {
                        if (header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            authorization = header["Proxy-Authorization:".Length..].Trim();
                        }
                    }

                    string response;
                    if (authorization is null)
                    {
                        response = "HTTP/1.1 407 Proxy Authentication Required\r\n"
                            + "Proxy-Authenticate: Basic realm=\"proxy\"\r\nContent-Length: 0\r\n\r\n";
                    }
                    else
                    {
                        Authorization = authorization;
                        response = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                    if (authorization is not null)
                    {
                        return;
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
