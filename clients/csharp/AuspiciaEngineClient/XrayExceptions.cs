using System;

namespace Auspicia.Engine;

/// <summary>Base for Portfolio X-ray ingestion errors. StatusCode 0 means transport/network failure.</summary>
public class XrayIngestException : Exception
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public XrayIngestException(string message, int statusCode, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

/// <summary>401 / 403 — the API key is missing, invalid, or not authorized for the tenant.</summary>
public sealed class XrayAuthException : XrayIngestException
{
    public XrayAuthException(string message, int statusCode, string? responseBody = null)
        : base(message, statusCode, responseBody) { }
}

/// <summary>400 / 413 / 422 — terminal request-shape or CSV-shape problem. Fix the payload before retrying.</summary>
public sealed class XrayRequestException : XrayIngestException
{
    public ProblemDetails? Problem { get; }

    public XrayRequestException(string message, int statusCode, string? responseBody = null, ProblemDetails? problem = null)
        : base(message, statusCode, responseBody) => Problem = problem;
}
