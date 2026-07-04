using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Auspicia.Engine;

/// <summary>
/// Client for the Auspicia Portfolio X-ray ingestion API. This is separate from daily engine-run
/// submission: it bulk-loads historical allocation/NAV CSVs and returns per-item partial-success errors.
/// </summary>
public sealed class AuspiciaXrayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    /// <param name="baseUrl">e.g. "https://app.auspicia.io/api".</param>
    /// <param name="bearerToken">Optional Auspicia API/service bearer token. Omit if auth is already on httpClient.</param>
    /// <param name="httpClient">Optional shared HttpClient; one is created + disposed if null.</param>
    /// <param name="defaultHeaders">Optional extra headers, e.g. Cloudflare Access service-token headers.</param>
    /// <param name="maxRetries">Retry budget for transport errors and 5xx responses.</param>
    public AuspiciaXrayClient(
        string baseUrl,
        string? bearerToken = null,
        HttpClient? httpClient = null,
        IReadOnlyDictionary<string, string>? defaultHeaders = null,
        int maxRetries = 4)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = TimeSpan.FromMilliseconds(250);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (defaultHeaders is not null)
        {
            foreach (var (name, value) in defaultHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Bulk-load historical allocation/performance CSVs. 201 and 207 both return
    /// <see cref="XrayBulkImportResult"/>; inspect <c>Errors</c> for per-item failures.
    /// </summary>
    public async Task<XrayBulkImportResult> BulkImportAsync(
        IReadOnlyList<XrayPortfolioImport> portfolios,
        CancellationToken ct = default)
    {
        return await BulkImportAsync(portfolios, targetOrgId: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bulk-load historical allocation/performance CSVs for a specific server-authorized organization.
    /// Pass a top-level targetOrgId only; per-item org targeting is rejected by the API.
    /// </summary>
    public async Task<XrayBulkImportResult> BulkImportAsync(
        IReadOnlyList<XrayPortfolioImport> portfolios,
        string? targetOrgId,
        CancellationToken ct = default)
    {
        if (portfolios is null) throw new ArgumentNullException(nameof(portfolios));
        var body = new XrayBulkImportRequest { Portfolios = portfolios, TargetOrgId = targetOrgId };
        return await BulkImportAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bulk-load historical allocation/performance CSVs. Request-shape failures throw
    /// <see cref="XrayRequestException"/>; per-item failures are returned inside the result.
    /// </summary>
    public async Task<XrayBulkImportResult> BulkImportAsync(
        XrayBulkImportRequest request,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        using var resp = await SendAsync(HttpMethod.Post, "xray/portfolios:bulk", () => JsonContent(request), ct)
            .ConfigureAwait(false);
        return (await ReadAsync<XrayBulkImportResult>(resp, ct, 201, 207).ConfigureAwait(false))!;
    }

    /// <summary>
    /// List organizations this authenticated API identity may target for X-ray/daily portfolio ingestion.
    /// Use <see cref="XrayIngestionTargetsResult.DefaultOrgId"/> when you do not need to override the org.
    /// </summary>
    public async Task<XrayIngestionTargetsResult> ListIngestionTargetsAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, "orgs/ingestion-targets", null, ct)
            .ConfigureAwait(false);
        return (await ReadAsync<XrayIngestionTargetsResult>(resp, ct, 200).ConfigureAwait(false))!;
    }

    /// <summary>Start an X-ray analysis job after a successful import. Defaults are server-side (topN=8).</summary>
    public async Task<XrayStartAnalysisResult> StartAnalysisAsync(
        string portfolioId,
        XrayAnalysisRequest? request = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(portfolioId)) throw new ArgumentException("portfolioId is required", nameof(portfolioId));
        var path = $"xray/portfolios/{Uri.EscapeDataString(portfolioId)}/analyses";
        using var resp = await SendAsync(HttpMethod.Post, path, () => JsonContent(request ?? new XrayAnalysisRequest()), ct)
            .ConfigureAwait(false);
        return (await ReadAsync<XrayStartAnalysisResult>(resp, ct, 202).ConfigureAwait(false))!;
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, AuspiciaEngineClient.Json), Encoding.UTF8, "application/json");

    // Retries transport errors and 5xx. The final 5xx response is returned to ReadAsync so callers get
    // the server body in XrayIngestException.ResponseBody.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_baseDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(method, path);
                if (content is not null) req.Content = content();
                var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500 || attempt == _maxRetries)
                    return resp;
                last = new XrayIngestException($"server error {(int)resp.StatusCode}", (int)resp.StatusCode);
                resp.Dispose();
            }
            catch (HttpRequestException ex) { last = ex; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }
        }

        throw last as XrayIngestException
              ?? new XrayIngestException($"request failed after {_maxRetries + 1} attempts: {last?.Message}", 0, inner: last);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct, params int[] okStatuses)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        foreach (var ok in okStatuses)
        {
            if (code == ok)
                return JsonSerializer.Deserialize<T>(text, AuspiciaEngineClient.Json);
        }
        if (code is 401 or 403)
            throw new XrayAuthException($"authentication failed ({code}): {DetailOrBody(text)}", code, text);
        if (code is 400 or 413 or 422)
            throw new XrayRequestException(DetailOrBody(text), code, text, TryProblem(text));
        throw new XrayIngestException($"unexpected status {code}: {DetailOrBody(text)}", code, text);
    }

    private static ProblemDetails? TryProblem(string text)
    {
        try { return JsonSerializer.Deserialize<ProblemDetails>(text, AuspiciaEngineClient.Json); }
        catch { return null; }
    }

    private static string DetailOrBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "empty response body";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? text : detail.GetRawText();
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString() ?? text;
        }
        catch { /* plain-text body */ }
        return text;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
