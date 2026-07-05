using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Auspicia.Engine;

/// <summary>
/// Client for the Auspicia engine ingestion API. Submit daily optimizer runs — idempotent,
/// checksum-verified, and automatically retried on transient (5xx / network) failures.
///
/// <code>
/// using var client = new AuspiciaEngineClient("https://app.auspicia.io/api", token);
/// var run = new EngineRun { RunId = "vulkan-2026-06-18", EngineKey = "vulkan-optimizer",
///                           Kind = "optimization", AsOf = "2026-06-18", Positions = positions };
/// await client.ValidateAsync(run);          // dry-run once during onboarding
/// var result = await client.SubmitAsync(run); // daily; safe to retry the same run
/// </code>
/// </summary>
public sealed class AuspiciaEngineClient : IDisposable
{
    /// <summary>The exact JSON options the API expects (camelCase, omit nulls).</summary>
    public static readonly JsonSerializerOptions Json = AuspiciaJsonContext.Default.Options;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    /// <param name="baseUrl">e.g. "https://app.auspicia.io/api".</param>
    /// <param name="token">The API key bearer token issued to you. Legacy engine tokens also work on engine-run routes.</param>
    /// <param name="httpClient">optional shared HttpClient; one is created + disposed if null.</param>
    /// <param name="maxRetries">transient-failure retry budget (per call).</param>
    public AuspiciaEngineClient(string baseUrl, string token, HttpClient? httpClient = null, int maxRetries = 4)
        : this(baseUrl, token, httpClient, defaultHeaders: null, maxRetries: maxRetries)
    {
    }

    /// <param name="baseUrl">e.g. "https://app.auspicia.io/api".</param>
    /// <param name="token">The API key bearer token issued to you. Legacy engine tokens also work on engine-run routes.</param>
    /// <param name="httpClient">optional shared HttpClient; one is created + disposed if null.</param>
    /// <param name="defaultHeaders">Optional extra headers, e.g. Cloudflare Access service-token headers.</param>
    /// <param name="maxRetries">transient-failure retry budget (per call).</param>
    public AuspiciaEngineClient(
        string baseUrl,
        string token,
        HttpClient? httpClient = null,
        IReadOnlyDictionary<string, string>? defaultHeaders = null,
        int maxRetries = 4)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = TimeSpan.FromMilliseconds(250);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (defaultHeaders is not null)
        {
            foreach (var (name, value) in defaultHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>Wrap a run in an envelope. idempotencyKey defaults to RunId; checksum is computed by default.</summary>
    public static EngineEnvelope BuildEnvelope(EngineRun run, string? idempotencyKey = null, bool withChecksum = true) =>
        new()
        {
            IdempotencyKey = idempotencyKey ?? run.RunId,
            Producer = new Producer { EngineKey = run.EngineKey },
            EmittedAt = DateTimeOffset.UtcNow.ToString("o"),
            Checksum = withChecksum ? EngineChecksum.Compute(run) : null,
            Run = run,
        };

    /// <summary>Dry-run: validate + price the run WITHOUT persisting. Use in onboarding / CI.</summary>
    public async Task<ValidateResult> ValidateAsync(EngineRun run, CancellationToken ct = default)
    {
        var body = BuildEnvelope(run);
        using var resp = await SendAsync(HttpMethod.Post, "v1/engine-runs:validate", () => JsonContent(body), ct).ConfigureAwait(false);
        return (await ReadAsync(resp, ct, AuspiciaJsonContext.Default.ValidateResult).ConfigureAwait(false))!;
    }

    /// <summary>Submit a run. Idempotent on (engineKey, idempotencyKey) — retrying the same run is safe.</summary>
    public async Task<IngestResult> SubmitAsync(EngineRun run, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = BuildEnvelope(run, idempotencyKey);
        using var resp = await SendAsync(HttpMethod.Post, "v1/engine-runs", () => JsonContent(body), ct).ConfigureAwait(false);
        return (await ReadAsync(resp, ct, AuspiciaJsonContext.Default.IngestResult).ConfigureAwait(false))!;
    }

    /// <summary>Submit a legacy TradeWeights CSV (first column = date, remaining columns = signed weights).</summary>
    public async Task<IngestResult> SubmitCsvAsync(string csv, string engineKey, string? asOf = null, CancellationToken ct = default)
    {
        var path = $"v1/engine-runs:csv?engineKey={Uri.EscapeDataString(engineKey)}"
                   + (asOf is null ? "" : $"&asOf={Uri.EscapeDataString(asOf)}");
        using var resp = await SendAsync(HttpMethod.Post, path, () => new StringContent(csv, Encoding.UTF8, "text/csv"), ct).ConfigureAwait(false);
        return (await ReadAsync(resp, ct, AuspiciaJsonContext.Default.IngestResult).ConfigureAwait(false))!;
    }

    /// <summary>Latest accepted optimizer weight for a symbol (raw JSON).</summary>
    public async Task<JsonElement> GetLatestAsync(string symbol, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"v1/securities/{Uri.EscapeDataString(symbol)}/optimizer/latest", null, ct).ConfigureAwait(false);
        return await ReadJsonElementAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Discover the parameters registered for an engine (the producer-driven registry). Handy to
    /// confirm your declared types landed as expected. Pass engineKey to scope, or null for all.</summary>
    public async Task<IReadOnlyList<ParameterInfo>> GetParametersAsync(string? engineKey = null, CancellationToken ct = default)
    {
        var path = "v1/engine-parameters" + (engineKey is null ? "" : $"?engineKey={Uri.EscapeDataString(engineKey)}");
        using var resp = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var wrap = await ReadAsync(resp, ct, AuspiciaJsonContext.Default.ParameterListResult).ConfigureAwait(false);
        return wrap?.Parameters ?? Array.Empty<ParameterInfo>();
    }

    private static StringContent JsonContent(EngineEnvelope body) =>
        new(JsonSerializer.Serialize(body, AuspiciaJsonContext.Default.EngineEnvelope), Encoding.UTF8, "application/json");

    // Sends with exponential backoff on transport errors + 5xx. 2xx/4xx are returned as-is (terminal);
    // the content factory is re-invoked per attempt because HttpContent cannot be re-sent.
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
                if ((int)resp.StatusCode < 500) return resp;   // terminal (2xx / 4xx)
                last = new EngineIngestException($"server error {(int)resp.StatusCode}", (int)resp.StatusCode);
                resp.Dispose();
            }
            catch (HttpRequestException ex) { last = ex; }                                        // network
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }    // timeout
        }
        throw last as EngineIngestException
              ?? new EngineIngestException($"request failed after {_maxRetries + 1} attempts: {last?.Message}", 0, last);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct, JsonTypeInfo<T> jsonTypeInfo)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 200 or 201)
            return JsonSerializer.Deserialize(text, jsonTypeInfo);
        if (code == 422)
        {
            ProblemDetails? problem = null;
            try { problem = JsonSerializer.Deserialize(text, AuspiciaJsonContext.Default.ProblemDetails); } catch { /* not problem+json */ }
            throw new EngineValidationException(problem?.Detail ?? "invalid engine run", problem);
        }
        if (code is 401 or 403)
            throw new EngineAuthException($"authentication failed ({code}): {text}", code);
        throw new EngineIngestException($"unexpected status {code}: {text}", code);
    }

    private static async Task<JsonElement> ReadJsonElementAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 200 or 201)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        if (code is 401 or 403)
            throw new EngineAuthException($"authentication failed ({code}): {text}", code);
        throw new EngineIngestException($"unexpected status {code}: {text}", code);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
