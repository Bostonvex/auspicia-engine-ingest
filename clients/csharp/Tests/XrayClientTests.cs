using Auspicia.Engine;
using Xunit;

namespace Auspicia.Engine.Tests;

public class XrayClientTests
{
    [Fact]
    public async Task BulkImport_returns_multistatus_with_item_errors()
    {
        var handler = new FakeHandler(FakeHandler.Response(207, """
        {
          "imported": [
            {
              "index": 0,
              "portfolio": { "id": "xray_1", "name": "Good", "hasAllocations": true, "hasPerformance": false },
              "parseReport": { "allocRows": 1, "perfRows": 0, "warnings": [], "cashColumn": false }
            }
          ],
          "errors": [{ "index": 1, "status": 422, "detail": "Upload allocations and/or performance CSV." }],
          "count": 1,
          "failed": 1
        }
        """));
        using var client = Client(handler);

        var result = await client.BulkImportAsync(new[]
        {
            new XrayPortfolioImport { Name = "Good", AllocationsCsv = "Date,AAPL\n2026-01-01,1\n" },
            new XrayPortfolioImport { Name = "Bad" },
        });

        Assert.True(result.HasFailures);
        Assert.Equal(1, result.Count);
        Assert.Equal(1, result.Failed);
        Assert.Equal("xray_1", result.Imported[0].Portfolio?.Id);
        Assert.Equal(422, result.Errors[0].Status);
        Assert.Equal("Upload allocations and/or performance CSV.", result.Errors[0].Detail);
    }

    [Fact]
    public async Task BulkImport_throws_typed_request_exception_on_413()
    {
        var handler = new FakeHandler(FakeHandler.Response(413, """{"detail":"Bulk ingest supports at most 250 portfolios per request."}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<XrayRequestException>(() => client.BulkImportAsync(new[]
        {
            new XrayPortfolioImport { Name = "Too much", AllocationsCsv = "Date,AAPL\n2026-01-01,1\n" },
        }));

        Assert.Equal(413, ex.StatusCode);
        Assert.Contains("250 portfolios", ex.Message);
        Assert.Contains("250 portfolios", ex.ResponseBody);
    }

    [Fact]
    public async Task BulkImport_retries_503_then_succeeds()
    {
        var handler = new FakeHandler(
            FakeHandler.Response(503, """{"detail":"Database unavailable."}"""),
            FakeHandler.Response(201, """{"imported":[],"errors":[],"count":0,"failed":0}"""));
        using var client = Client(handler, maxRetries: 1);

        var result = await client.BulkImportAsync(new[]
        {
            new XrayPortfolioImport { Name = "Retry", AllocationsCsv = "Date,AAPL\n2026-01-01,1\n" },
        });

        Assert.False(result.HasFailures);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("api/xray/portfolios:bulk", r.Path));
    }

    [Fact]
    public async Task BulkImport_sends_bearer_and_default_headers()
    {
        var handler = new FakeHandler(FakeHandler.Response(201, """{"imported":[],"errors":[],"count":0,"failed":0}"""));
        using var client = Client(
            handler,
            bearerToken: "svc-token",
            headers: new Dictionary<string, string> { ["CF-Access-Client-Id"] = "cf-id" });

        await client.BulkImportAsync(new[]
        {
            new XrayPortfolioImport { Name = "Headers", AllocationsCsv = "Date,AAPL\n2026-01-01,1\n" },
        });

        var req = handler.Requests.Single();
        Assert.Equal("Bearer", req.Authorization?.Scheme);
        Assert.Equal("svc-token", req.Authorization?.Parameter);
        Assert.Equal("cf-id", req.Headers["CF-Access-Client-Id"].Single());
        Assert.Contains("\"portfolios\"", req.Body);
    }

    [Fact]
    public async Task BulkImport_sends_top_level_target_org()
    {
        var handler = new FakeHandler(FakeHandler.Response(201, """{"imported":[],"errors":[],"count":0,"failed":0}"""));
        using var client = Client(handler);

        await client.BulkImportAsync(
            new[]
            {
                new XrayPortfolioImport { Name = "LampShade", AllocationsCsv = "Date,AAPL\n2026-01-01,1\n" },
            },
            targetOrgId: "lampshade");

        var req = handler.Requests.Single();
        Assert.Contains("\"targetOrgId\":\"lampshade\"", req.Body);
        Assert.DoesNotContain("\"portfolios\":[{\"targetOrgId\"", req.Body);
    }

    [Fact]
    public async Task ListIngestionTargets_reads_allowed_orgs()
    {
        var handler = new FakeHandler(FakeHandler.Response(200, """
        {
          "orgs": [
            { "id": "auspicia", "displayName": "Auspicia", "status": "active", "role": "platform-admin" },
            { "id": "lampshade", "displayName": "LampShade", "status": "active", "role": "org-admin" }
          ],
          "defaultOrgId": "auspicia"
        }
        """));
        using var client = Client(handler);

        var result = await client.ListIngestionTargetsAsync();

        Assert.Equal("auspicia", result.DefaultOrgId);
        Assert.Equal(2, result.Orgs.Count);
        Assert.Equal("lampshade", result.Orgs[1].Id);
        Assert.Equal("LampShade", result.Orgs[1].DisplayName);
        var req = handler.Requests.Single();
        Assert.Equal("GET", req.Method);
        Assert.Equal("api/orgs/ingestion-targets", req.Path);
        Assert.Equal("", req.Body);
    }

    [Fact]
    public async Task StartAnalysis_accepts_202()
    {
        var handler = new FakeHandler(FakeHandler.Response(202, """
        {
          "analysisId": "xray_analysis_1",
          "analysis": { "id": "xray_analysis_1", "portfolioId": "xray_1", "status": "queued", "stage": "queued" }
        }
        """));
        using var client = Client(handler);

        var result = await client.StartAnalysisAsync("xray_1", new XrayAnalysisRequest { ThresholdPct = 10, TopN = 8 });

        Assert.Equal("xray_analysis_1", result.AnalysisId);
        Assert.Equal("xray_1", result.Analysis?.PortfolioId);
        Assert.Equal("api/xray/portfolios/xray_1/analyses", handler.Requests.Single().Path);
    }

    private static AuspiciaXrayClient Client(
        FakeHandler handler,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxRetries = 0)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        return new AuspiciaXrayClient(
            "https://example.test/api",
            bearerToken: bearerToken,
            httpClient: http,
            defaultHeaders: headers,
            maxRetries: maxRetries);
    }

}
