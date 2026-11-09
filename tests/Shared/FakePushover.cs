using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ChasePie.Lookout.Tests;

/// <summary>
/// A request <see cref="FakePushover"/> received, with its form's fields and files.
/// </summary>
internal sealed record SentRequest(
    HttpMethod Method,
    Uri Uri,
    Dictionary<string, string> Fields,
    Dictionary<string, (byte[] Bytes, MediaTypeHeaderValue? Type, string? FileName)> Files);

/// <summary>
/// Answers every request with <see cref="Status"/> and <paramref name="body"/>, or throws <paramref name="fail"/>, and
/// keeps each request's form.
/// </summary>
internal sealed class FakePushover(
    HttpStatusCode status = HttpStatusCode.OK,
    string body = """{"status":1,"request":"647d2300-702c-4b38-8b2f-d56326ae460b"}""",
    Exception? fail = null) : HttpMessageHandler
{
    private readonly List<SentRequest> _requests = [];

    /// <summary>
    /// The status to answer the next requests with, <paramref name="status"/> to start with.
    /// </summary>
    public HttpStatusCode Status { get; set; } = status;

    public IReadOnlyList<SentRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Waits, for up to 10 seconds of real time, until at least <paramref name="count"/> requests have come.
    /// </summary>
    public async Task WaitForRequestsAsync(int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (Requests.Count < count)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>();
        var files = new Dictionary<string, (byte[], MediaTypeHeaderValue?, string?)>();
        if (request.Content is MultipartFormDataContent form)
        {
            foreach (var part in form)
            {
                var disposition = part.Headers.ContentDisposition!;
                var name = disposition.Name!.Trim('"');
                if (disposition.FileName is { } fileName)
                {
                    files[name] = (await part.ReadAsByteArrayAsync(cancellationToken), part.Headers.ContentType,
                        fileName.Trim('"'));
                }
                else
                {
                    fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }
        }

        lock (_requests)
        {
            _requests.Add(new SentRequest(request.Method, request.RequestUri!, fields, files));
        }

        if (fail is not null)
        {
            throw fail;
        }

        return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
