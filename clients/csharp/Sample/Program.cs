// Minimal end-to-end sample: build a run, dry-run validate it, then submit it.
// Run with:
//   AUSPICIA_API_KEY=ak_live_... AUSPICIA_BASE_URL=https://staging.auspicia.io/api dotnet run
using Auspicia.Engine;

var baseUrl = Environment.GetEnvironmentVariable("AUSPICIA_BASE_URL") ?? "https://app.auspicia.io/api";
var token = Environment.GetEnvironmentVariable("AUSPICIA_API_KEY")
            ?? Environment.GetEnvironmentVariable("AUSPICIA_ENGINE_TOKEN")
            ?? throw new InvalidOperationException("Set AUSPICIA_API_KEY.");
var engineKey = Environment.GetEnvironmentVariable("AUSPICIA_ENGINE_KEY") ?? "vulkan-optimizer";
var headers = new Dictionary<string, string>();
if (Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_ID") is { Length: > 0 } cfId)
    headers["CF-Access-Client-Id"] = cfId;
if (Environment.GetEnvironmentVariable("CF_ACCESS_CLIENT_SECRET") is { Length: > 0 } cfSecret)
    headers["CF-Access-Client-Secret"] = cfSecret;

var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd");
var run = new EngineRun
{
    RunId = $"{engineKey}-{asOf}",
    EngineKey = engineKey,
    Kind = "optimization",
    Economics = "implementation",
    Modality = "gpu-batch",
    AsOf = asOf,
    Universe = "US-SP500",
    Ccy = "USD",
    SolveMs = 412,
    // Declare the parameters this run carries — the platform consumes them by definition.
    ParameterDefs = new List<ParameterDefinition>
    {
        new() { Key = "momentum",   Type = "number",  Unit = "z", Direction = "higher_better", Label = "Momentum (z)" },
        new() { Key = "conviction", Type = "integer", Range = new[] { 1.0, 10.0 }, Direction = "higher_better" },
        new() { Key = "regime",     Type = "enum",    Values = new[] { "risk_on", "risk_off" } },
    },
    Positions = new List<EnginePosition>
    {
        new() { Ticker = "AAPL", Weight = 4.10, Score = 0.62, Rank = 1,
                Params = new Dictionary<string, EngineJsonValue> { ["momentum"] = 0.73, ["conviction"] = 8, ["regime"] = "risk_on" } },
        new() { Ticker = "NVDA", Weight = -3.25, Score = -0.48, Rank = 2,
                Params = new Dictionary<string, EngineJsonValue> { ["momentum"] = -0.11, ["conviction"] = 3, ["regime"] = "risk_off" } },
    },
};

using var client = new AuspiciaEngineClient(baseUrl, token, defaultHeaders: headers);

try
{
    Console.WriteLine($"local checksum : {EngineChecksum.Compute(run)}");

    var check = await client.ValidateAsync(run);
    Console.WriteLine($"validate       : valid={check.Valid} positions={check.Positions} " +
                      $"params={check.ParameterDefs} coercions={check.CoercionWarnings?.Count ?? 0} " +
                      $"gross={check.Exposure?.Gross} server-checksum={check.Checksum}");

    var result = await client.SubmitAsync(run);   // idempotent: re-running prints deduped=true
    Console.WriteLine($"submit         : status={result.Status} deduped={result.Deduped} id={result.Id}");
    if (result.ParametersRegistered is { Count: > 0 } reg)
        Console.WriteLine($"                 registered params: {string.Join(", ", reg)}");
    if (result.Supersedes is { Count: > 0 } s)
        Console.WriteLine($"                 superseded {s.Count} prior run(s)");

    var registry = await client.GetParametersAsync(engineKey);   // discovery
    Console.WriteLine($"parameters     : {registry.Count} registered");
    foreach (var p in registry)
        Console.WriteLine($"                 {p.Key,-12} {p.Type,-8} {p.Unit} {p.Direction}");
}
catch (EngineValidationException ex)
{
    Console.Error.WriteLine($"VALIDATION FAILED (fix the payload — do not retry unchanged): {ex.Message}");
    foreach (var e in ex.Problem?.Errors ?? Array.Empty<ProblemError>())
        Console.Error.WriteLine($"  - {e.Path}: {e.Msg}");
    if (ex.Problem?.DeadletterId is { } dl) Console.Error.WriteLine($"  dead-letter id: {dl}");
    Environment.Exit(1);
}
catch (EngineAuthException ex)
{
    Console.Error.WriteLine($"AUTH FAILED (check token / engineKey): {ex.Message}");
    Environment.Exit(1);
}
catch (EngineIngestException ex)
{
    Console.Error.WriteLine($"INGEST FAILED (status={ex.StatusCode}) after retries: {ex.Message}");
    Environment.Exit(2);
}
