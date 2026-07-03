# Auspicia Engine Client (C#)

A small .NET 8 library for pushing daily optimizer runs — with parameters — into the Auspicia ingestion
API. It handles the envelope shape, the integrity **checksum** (including parameters), **bearer auth**,
**idempotency**, and **retry/backoff**. Pairs with the [integration guide](../../docs/INTEGRATION-GUIDE.md).

- **Target:** .NET 8. No external dependencies (uses `System.Text.Json` + `HttpClient`).
- **Layout:** `AuspiciaEngineClient/` (the library) · `Sample/` (a runnable console example) ·
  `Tests/` (a checksum-parity test against the shared reference vectors).

## Install / build

Copy the `AuspiciaEngineClient/` project into your solution and add a project reference, or build it as a
package:

```bash
dotnet build AuspiciaEngineClient   # build the library
dotnet pack  AuspiciaEngineClient   # -> Auspicia.Engine.Client.1.0.0.nupkg
```

## Usage

```csharp
using Auspicia.Engine;

using var client = new AuspiciaEngineClient(
    baseUrl: "https://app.auspicia.io/api",   // provisioned per-integration
    token:   Environment.GetEnvironmentVariable("AUSPICIA_ENGINE_TOKEN")!);

var run = new EngineRun
{
    RunId     = "vulkan-2026-06-18",
    EngineKey = "vulkan-optimizer",
    Kind      = "optimization",
    AsOf      = "2026-06-18",            // YYYY-MM-DD
    Universe  = "US-SP500",
    ParameterDefs = new List<ParameterDefinition>
    {
        new() { Key = "momentum",   Type = "number",  Unit = "z", Direction = "higher_better" },
        new() { Key = "conviction", Type = "integer", Range = new[] { 1.0, 10.0 } },
        new() { Key = "regime",     Type = "enum",    Values = new[] { "risk_on", "risk_off" } },
    },
    Positions = new List<EnginePosition>
    {
        new() { Ticker = "AAPL", Weight =  4.10, Score =  0.62, Rank = 1,
                Params = new Dictionary<string, object> { ["momentum"] =  0.73, ["conviction"] = 8, ["regime"] = "risk_on"  } },
        new() { Ticker = "NVDA", Weight = -3.25, Score = -0.48, Rank = 2,
                Params = new Dictionary<string, object> { ["momentum"] = -0.11, ["conviction"] = 3, ["regime"] = "risk_off" } },
    },
};

// 1) dry-run during onboarding / CI — validates + prices + previews coercions, writes nothing
ValidateResult check = await client.ValidateAsync(run);
Console.WriteLine($"valid={check.Valid}, params={check.ParameterDefs}, coercions={check.CoercionWarnings?.Count ?? 0}");

// 2) submit daily. Idempotent — if the push fails, just call SubmitAsync(run) again with the same run.
IngestResult result = await client.SubmitAsync(run);
Console.WriteLine($"{result.Status} (deduped={result.Deduped}), registered=[{string.Join(",", result.ParametersRegistered ?? new List<string>())}]");

// 3) discover what's registered
foreach (var p in await client.GetParametersAsync("vulkan-optimizer"))
    Console.WriteLine($"{p.Key}: {p.Type} {p.Unit} ({p.Direction})");
```

### What the client does for you
- **Envelope + checksum:** wraps your `EngineRun` in the canonical envelope, sets `idempotencyKey` to
  `RunId` (override via the argument), and attaches the integrity checksum (`EngineChecksum.Compute`) —
  including declared parameters, byte-identical to the server.
- **Idempotency:** re-submitting the same run returns the stored run with `Deduped = true`. Retrying after a
  network failure is always safe.
- **Retries:** transient failures (5xx, network, timeout) are retried with exponential backoff
  (`maxRetries`, default 4). `4xx` are terminal.
- **Typed errors:** `EngineValidationException` (422, with `Problem` = the RFC 7807 body + field errors),
  `EngineAuthException` (401/403), `EngineIngestException` (other/transport). See the sample for handling.

### Parameters
Declare parameters in `EngineRun.ParameterDefs`; attach values per name in `EnginePosition.Params`
(`Dictionary<string, object>`). Values can be `double`, `int`/`long`, `bool`, `string`, a numeric vector
(`double[]` / `IReadOnlyList<double>`), or a nested object for a `json` param. See
[Dynamic parameters](../../docs/PARAMETERS.md).

### CSV (legacy, weights-only)
```csharp
await client.SubmitCsvAsync(csvText, engineKey: "vulkan-optimizer", asOf: "2026-06-18");
```

## Run the sample

```bash
export AUSPICIA_ENGINE_TOKEN=eng_staging_xxxxx
export AUSPICIA_BASE_URL=https://staging.auspicia.io/api   # optional; defaults to production
dotnet run --project Sample
```

## Verify checksum parity

The library ships with a test that recomputes the checksum for every case in
[`schema/checksum-test-vectors.json`](../../schema/checksum-test-vectors.json) and asserts it matches
byte-for-byte. Run it once when you adopt or port the client:

```bash
dotnet test Tests
```

## Going live

Follow the checklist in [the integration guide](../../docs/INTEGRATION-GUIDE.md#going-live--checklist):
validate on staging → confirm the checksum matches `check.Checksum` and the reference vectors → submit +
confirm a retry dedups → switch to the production token/URL → schedule the daily push.
