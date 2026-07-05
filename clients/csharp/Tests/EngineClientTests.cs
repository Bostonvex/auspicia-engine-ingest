using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auspicia.Engine;
using Xunit;

namespace Auspicia.Engine.Tests;

public class EngineClientTests
{
    [Fact]
    public async Task Submit_serializes_dynamic_params_with_source_generated_json()
    {
        var handler = new FakeHandler(Response(201, """
        {
          "id": "run_1",
          "runId": "native-2026-07-04",
          "engineKey": "native-engine",
          "status": "accepted",
          "deduped": false,
          "positions": 1,
          "asOf": "2026-07-04",
          "parametersRegistered": ["momentum", "regime", "embedding", "explain"]
        }
        """));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        using var client = new AuspiciaEngineClient("https://example.test/api", "ak_test", httpClient: http, maxRetries: 0);

        var run = new EngineRun
        {
            RunId = "native-2026-07-04",
            EngineKey = "native-engine",
            AsOf = "2026-07-04",
            ParameterDefs = new[]
            {
                new ParameterDefinition { Key = "momentum", Type = "number" },
                new ParameterDefinition { Key = "regime", Type = "enum", Values = new[] { "risk_on", "risk_off" } },
                new ParameterDefinition { Key = "embedding", Type = "vector", Dim = 2 },
                new ParameterDefinition { Key = "explain", Type = "json" },
            },
            Positions = new[]
            {
                new EnginePosition
                {
                    Ticker = "AAPL",
                    Weight = 4.10,
                    Params = new Dictionary<string, EngineJsonValue>
                    {
                        ["momentum"] = 0.73,
                        ["regime"] = "risk_on",
                        ["embedding"] = new[] { 1.0, 2.5 },
                        ["explain"] = new Dictionary<string, EngineJsonValue>
                        {
                            ["source"] = "native-aot",
                            ["rank"] = 1,
                        },
                    },
                },
            },
        };

        var result = await client.SubmitAsync(run);

        Assert.Equal("accepted", result.Status);
        var req = handler.Requests.Single();
        Assert.Equal("POST", req.Method);
        Assert.Equal("api/v1/engine-runs", req.Path);
        Assert.Equal("Bearer", req.Authorization?.Scheme);
        Assert.Contains("\"params\"", req.Body);
        Assert.Contains("\"momentum\":0.73", req.Body);
        Assert.Contains("\"regime\":\"risk_on\"", req.Body);
        Assert.Contains("\"embedding\":[1,2.5]", req.Body);
        Assert.Contains("\"explain\":{\"source\":\"native-aot\",\"rank\":1}", req.Body);
    }

    [Fact]
    public async Task Submit_sends_bearer_and_default_headers()
    {
        var handler = new FakeHandler(Response(201, """
        {
          "id": "run_1",
          "runId": "headers-2026-07-05",
          "engineKey": "native-engine",
          "status": "accepted",
          "deduped": false,
          "positions": 1,
          "asOf": "2026-07-05"
        }
        """));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        using var client = new AuspiciaEngineClient(
            "https://example.test/api",
            "ak_test",
            httpClient: http,
            defaultHeaders: new Dictionary<string, string> { ["CF-Access-Client-Id"] = "cf-id" },
            maxRetries: 0);

        await client.SubmitAsync(new EngineRun
        {
            RunId = "headers-2026-07-05",
            EngineKey = "native-engine",
            AsOf = "2026-07-05",
            Positions = new[] { new EnginePosition { Ticker = "AAPL", Weight = 1.0 } },
        });

        var req = handler.Requests.Single();
        Assert.Equal("Bearer", req.Authorization?.Scheme);
        Assert.Equal("ak_test", req.Authorization?.Parameter);
        Assert.Equal("cf-id", req.Headers["CF-Access-Client-Id"].Single());
    }

    private static HttpResponseMessage Response(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public FakeHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        public List<RequestCapture> Requests { get; } = [];

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

    private sealed record RequestCapture(
        string Method,
        string Path,
        AuthenticationHeaderValue? Authorization,
        Dictionary<string, List<string>> Headers,
        string Body);
}
