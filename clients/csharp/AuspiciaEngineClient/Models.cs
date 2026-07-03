using System.Collections.Generic;

namespace Auspicia.Engine;

// Request models — serialized with a camelCase naming policy (see AuspiciaEngineClient.Json).

/// <summary>Who produced the run.</summary>
public sealed record Producer
{
    public required string EngineKey { get; init; }
    public string? Version { get; init; }
}

/// <summary>
/// A producer-declared parameter. The engine tells Auspicia the shape of each per-name value it sends,
/// so the value can be registered (discoverable), type-coerced, and projected for filtering/ranking.
/// Declaring is how a NEW parameter becomes usable — the platform consumes by definition.
/// </summary>
public sealed record ParameterDefinition
{
    public required string Key { get; init; }
    /// <summary>One of: number, integer, boolean, string, enum, vector, json.</summary>
    public required string Type { get; init; }
    public string? Unit { get; init; }
    /// <summary>Vector length (required when Type == "vector").</summary>
    public int? Dim { get; init; }
    /// <summary>Ranking hint for the UI: higher_better | lower_better | neutral.</summary>
    public string? Direction { get; init; }
    /// <summary>Human-friendly display name.</summary>
    public string? Label { get; init; }
    public string? Description { get; init; }
    /// <summary>Allowed values (required when Type == "enum").</summary>
    public IReadOnlyList<string>? Values { get; init; }
    /// <summary>[min, max] hint for numeric UIs.</summary>
    public IReadOnlyList<double>? Range { get; init; }
}

/// <summary>One per-name row: a signed weight (% of capital), positive = long, negative = short.</summary>
public sealed record EnginePosition
{
    public required string Ticker { get; init; }
    public required double Weight { get; init; }
    public string? Side { get; init; }
    public double? Score { get; init; }
    public int? Rank { get; init; }
    public string? Figi { get; init; }
    public string? Cik { get; init; }
    public IDictionary<string, object>? Attributes { get; init; }
    /// <summary>Per-name declared-parameter values; keys must match a ParameterDefinition on the run.
    /// Values are CLR scalars (double, int, long, bool, string), a vector (IReadOnlyList&lt;double&gt;),
    /// or a nested object for a "json" param.</summary>
    public IDictionary<string, object>? Params { get; init; }
}

/// <summary>The optimizer's output for one trading day.</summary>
public sealed record EngineRun
{
    public required string RunId { get; init; }
    public required string EngineKey { get; init; }
    public string Kind { get; init; } = "optimization";
    public string? Economics { get; init; }
    public string? Modality { get; init; }
    /// <summary>Trading day as an ISO date string, "YYYY-MM-DD".</summary>
    public required string AsOf { get; init; }
    public string? Universe { get; init; }
    public string Ccy { get; init; } = "USD";
    public string? Substrate { get; init; }
    public int? SolveMs { get; init; }
    public int? AttributesPerName { get; init; }
    /// <summary>Declares the parameters this run carries per name. Optional — omit for a weights-only run.</summary>
    public IReadOnlyList<ParameterDefinition>? ParameterDefs { get; init; }
    public required IReadOnlyList<EnginePosition> Positions { get; init; }
}

/// <summary>The submission envelope wrapping one run.</summary>
public sealed record EngineEnvelope
{
    public string SchemaVersion { get; init; } = "1.0";
    public string? IdempotencyKey { get; init; }
    public required Producer Producer { get; init; }
    public string? EmittedAt { get; init; }
    public string? Checksum { get; init; }
    public required EngineRun Run { get; init; }
}

// Response models.

public sealed record Exposure
{
    public double Gross { get; init; }
    public double Net { get; init; }
    public double LongPct { get; init; }
    public double ShortPct { get; init; }
}

/// <summary>One flagged value: a param whose value did not match its declared type. The run is still
/// accepted (flag-and-keep); the value is quarantined and excluded from type-aware numeric filters.</summary>
public sealed record CoercionWarning
{
    public string? ParamKey { get; init; }
    public string? Ticker { get; init; }
    public string? DeclaredType { get; init; }
    public object? Received { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Result of a submit (POST /v1/engine-runs).</summary>
public sealed record IngestResult
{
    public string? Id { get; init; }
    public string? RunId { get; init; }
    public string? EngineKey { get; init; }
    public string? Status { get; init; }
    /// <summary>True when this idempotencyKey was already ingested (safe retry / duplicate).</summary>
    public bool Deduped { get; init; }
    public int Positions { get; init; }
    public string? AsOf { get; init; }
    public Exposure? Exposure { get; init; }
    /// <summary>Ids of prior runs this submission superseded (same engineKey + asOf).</summary>
    public IReadOnlyList<string>? Supersedes { get; init; }
    /// <summary>Parameter keys registered/refreshed in the discovery registry by this submission.</summary>
    public IReadOnlyList<string>? ParametersRegistered { get; init; }
    /// <summary>Values flagged during type coercion (empty on a clean run). Inspect to spot feed bugs.</summary>
    public IReadOnlyList<CoercionWarning>? CoercionWarnings { get; init; }
}

/// <summary>Result of a dry-run (POST /v1/engine-runs:validate).</summary>
public sealed record ValidateResult
{
    public bool Valid { get; init; }
    public string? RunId { get; init; }
    public string? EngineKey { get; init; }
    public string? AsOf { get; init; }
    public int Positions { get; init; }
    public Exposure? Exposure { get; init; }
    /// <summary>The checksum the server computed — diff against your own to confirm your implementation.</summary>
    public string? Checksum { get; init; }
    /// <summary>Number of parameter definitions the server saw on the envelope.</summary>
    public int ParameterDefs { get; init; }
    /// <summary>Values that WOULD be flagged on ingest — fix your feed before going live.</summary>
    public IReadOnlyList<CoercionWarning>? CoercionWarnings { get; init; }
}

/// <summary>One registered parameter from GET /v1/engine-parameters (the discovery registry).</summary>
public sealed record ParameterInfo
{
    public string? EngineKey { get; init; }
    public string? Key { get; init; }
    public string? Type { get; init; }
    public string? Unit { get; init; }
    public int? Dim { get; init; }
    public string? Direction { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Values { get; init; }
    public IReadOnlyList<double>? Range { get; init; }
    public string? SchemaVersion { get; init; }
    public string? FirstSeenAt { get; init; }
}

/// <summary>RFC 7807 problem+json body returned on a 422 validation failure.</summary>
public sealed record ProblemDetails
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int Status { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<ProblemError>? Errors { get; init; }
    public string? DeadletterId { get; init; }
}

public sealed record ProblemError
{
    public string? Path { get; init; }
    public string? Msg { get; init; }
}
