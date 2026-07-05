using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Auspicia.Engine.Tests;

/// <summary>Shared fake HTTP plumbing for the client tests: queued canned responses + request capture.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public FakeHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

    public List<RequestCapture> Requests { get; } = [];

    public static HttpResponseMessage Response(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(
            new RequestCapture(
                request.Method.Method,
                request.RequestUri?.PathAndQuery.TrimStart('/') ?? "",
                request.Headers.Authorization,
                request.Headers.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));

        if (_responses.Count == 0)
            throw new InvalidOperationException("No fake response queued.");
        return _responses.Dequeue();
    }
}

internal sealed record RequestCapture(
    string Method,
    string Path,
    AuthenticationHeaderValue? Authorization,
    Dictionary<string, List<string>> Headers,
    string Body);
