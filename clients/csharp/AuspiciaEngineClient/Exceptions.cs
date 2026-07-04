using System;

namespace Auspicia.Engine;

/// <summary>Base for ingest errors. <see cref="StatusCode"/> is the HTTP status (0 = network/transport).</summary>
public class EngineIngestException : Exception
{
    public int StatusCode { get; }
    public EngineIngestException(string message, int statusCode, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

/// <summary>401 / 403 — the API key is missing, invalid, or scoped to a different engineKey. Terminal.</summary>
public sealed class EngineAuthException : EngineIngestException
{
    public EngineAuthException(string message, int statusCode) : base(message, statusCode) { }
}

/// <summary>422 — the envelope failed validation or the checksum did not match. Terminal: fix and re-send.</summary>
public sealed class EngineValidationException : EngineIngestException
{
    public ProblemDetails? Problem { get; }
    public EngineValidationException(string message, ProblemDetails? problem)
        : base(message, 422) => Problem = problem;
}
