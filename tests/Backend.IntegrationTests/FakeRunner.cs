using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Answers runs with <see cref="Responses"/> in turn, then <see cref="Response"/>, as the Runner's JSON, and keeps the
/// requests.
/// </summary>
internal sealed class FakeRunner(object response) : HttpMessageHandler
{
    /// <summary>
    /// What the next runs end with, as the Runner sends it: <paramref name="response"/> to start with.
    /// </summary>
    public object Response { get; set; } = response;

    /// <summary>
    /// What the next runs end with, one each, before <see cref="Response"/>.
    /// </summary>
    public ConcurrentQueue<object> Responses { get; } = new();

    /// <summary>
    /// Runs while the script would, before the run is answered.
    /// </summary>
    public Func<Task>? WhileRunning { get; set; }

    public List<Uri> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        if (WhileRunning is { } whileRunning)
        {
            await whileRunning();
        }

        var response = Responses.TryDequeue(out var next) ? next : Response;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };
    }
}
