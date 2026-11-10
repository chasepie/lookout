using System.Net;
using System.Text;
using System.Text.Json;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Answers every run with <see cref="Response"/> as the Runner's JSON, and keeps the requests.
/// </summary>
internal sealed class FakeRunner(object response) : HttpMessageHandler
{
    /// <summary>
    /// What the next runs end with, as the Runner sends it: <paramref name="response"/> to start with.
    /// </summary>
    public object Response { get; set; } = response;

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(Response, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        });
    }
}
