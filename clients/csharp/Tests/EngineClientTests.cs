using Auspicia.Engine;
using Xunit;

namespace Auspicia.Engine.Tests;

public class EngineClientTests
{
    [Fact]
    public async Task Submit_serializes_dynamic_params_with_source_generated_json()
    {
        var handler = new FakeHandler(FakeHandler.Response(201, """
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
    public async Task Submit_surfaces_server_detail_when_retries_exhaust_on_5xx()
    {
        var handler = new FakeHandler(FakeHandler.Response(503, """{"detail":"Database unavailable."}"""));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        using var client = new AuspiciaEngineClient("https://example.test/api", "ak_test", httpClient: http, maxRetries: 0);

        var ex = await Assert.ThrowsAsync<EngineIngestException>(() => client.SubmitAsync(MinimalRun()));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("Database unavailable.", ex.Message);   // body no longer discarded on the final attempt
    }

    [Fact]
    public async Task Submit_sends_default_headers()
    {
        var handler = new FakeHandler(FakeHandler.Response(201, """{"status":"accepted","deduped":false,"positions":1}"""));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        using var client = new AuspiciaEngineClient(
            "https://example.test/api", "ak_test", httpClient: http, maxRetries: 0,
            defaultHeaders: new Dictionary<string, string> { ["CF-Access-Client-Id"] = "cf-id" });

        await client.SubmitAsync(MinimalRun());

        Assert.Equal("cf-id", handler.Requests.Single().Headers["CF-Access-Client-Id"].Single());
    }

    private static EngineRun MinimalRun() => new()
    {
        RunId = "native-2026-07-04",
        EngineKey = "native-engine",
        AsOf = "2026-07-04",
        Positions = new[] { new EnginePosition { Ticker = "AAPL", Weight = 4.10 } },
    };
}
