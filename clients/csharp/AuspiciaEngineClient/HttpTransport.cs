using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Auspicia.Engine;

/// <summary>
/// Shared HTTP plumbing for the Auspicia clients: retry/backoff for transient failures, and
/// error-body summarization. Internal — the public surface is the clients and their exception types.
/// </summary>
internal static class HttpTransport
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Sends with exponential backoff on transport errors and 5xx. 2xx/4xx responses return immediately
    /// (terminal). The <b>final</b> 5xx response is returned rather than thrown, so the caller can read
    /// the server's body into its typed exception. The content factory is re-invoked per attempt because
    /// HttpContent cannot be re-sent. <c>makeTransportError</c> builds the client's transport exception
    /// from (message, statusCode, inner).
    /// </summary>
    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http,
        HttpMethod method,
        string path,
        Func<HttpContent>? content,
        int maxRetries,
        Func<string, int, Exception?, Exception> makeTransportError,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(BaseDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(method, path);
                if (content is not null) req.Content = content();
                var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500 || attempt == maxRetries)
                    return resp;
                last = makeTransportError($"server error {(int)resp.StatusCode}", (int)resp.StatusCode, null);
                resp.Dispose();
            }
            catch (HttpRequestException ex) { last = ex; }                                        // network
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }    // timeout
        }
        // Only reachable when the final attempt failed at the transport layer (a final 5xx returns above).
        throw makeTransportError($"request failed after {maxRetries + 1} attempts: {last?.Message}", 0, last);
    }

    /// <summary>Best human-readable summary of an error body: problem+json `detail`, then `title`, then the raw text.</summary>
    internal static string DetailOrBody(string text)
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

    /// <summary>Parse an RFC 7807 body, or null when the body isn't problem+json.</summary>
    internal static ProblemDetails? TryProblem(string text)
    {
        try { return JsonSerializer.Deserialize(text, AuspiciaJsonContext.Default.ProblemDetails); }
        catch { return null; }
    }
}
