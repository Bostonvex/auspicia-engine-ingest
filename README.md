# Auspicia Engine Ingestion Kit

**Push daily optimizer/compute-engine runs into the Auspicia decision-intelligence platform.**

This is the public integration kit for engine platforms that produce quantitative signals — a vector of
signed per-name weights for a trading day, plus any parameters your engine computes (momentum, quality,
conviction, risk, embeddings, …). It contains everything you need to integrate in any language:

| | |
|---|---|
| 📘 **[Quick start](docs/QUICKSTART.md)** | Submit your first run in ~5 minutes (curl + C#). |
| 📗 **[Integration guide](docs/INTEGRATION-GUIDE.md)** | The complete contract: auth, envelope, endpoints, idempotency, errors, go-live. |
| 📙 **[Portfolio X-ray ingestion](docs/PORTFOLIO-XRAY-INGESTION.md)** | Bulk-load historical allocation/NAV CSVs for drawdown attribution and X-ray analysis. |
| 🧬 **[Dynamic parameters](docs/PARAMETERS.md)** | How to declare and send arbitrary, evolving parameters the platform consumes by definition. |
| 🔒 **[Checksum spec](docs/CHECKSUM.md)** | The language-stable integrity algorithm, with a percent-by-percent worked example. |
| 📐 **[JSON Schema](schema/envelope.schema.json)** | Machine-readable envelope contract — validate your payloads in CI. |
| ✅ **[Reference vectors](schema/checksum-test-vectors.json)** | Frozen checksum test cases every client must reproduce byte-for-byte. |
| 💻 **[C# client](clients/csharp/)** | A drop-in .NET 8 library (envelope, checksum, auth, idempotency, retries). |

---

## How it works

Your engine emits one **run** per trading day. You wrap it in an **envelope** and `POST` it over HTTPS:

```
        your engine                         Auspicia
   ┌───────────────────┐   POST /v1/engine-runs   ┌──────────────────────────────┐
   │  runId, asOf,     │  ───────────────────────▶ │  validate → checksum → store  │
   │  positions[],     │   Bearer <engine-token>   │  → supersede prior day        │
   │  parameterDefs[]  │  ◀─────────────────────── │  → register params            │
   └───────────────────┘   201 accepted / 200 dedup└──────────────────────────────┘
```

- **One run per `(engineKey, asOf)` wins.** The newest accepted run supersedes the prior; history is
  append-only (corrections never mutate — they supersede).
- **Idempotent.** Re-submitting the same `idempotencyKey` returns the stored run (`deduped: true`). On any
  network error, just retry the identical envelope.
- **Integrity-checked.** An optional `sha256` checksum over the run detects in-flight corruption and
  wrong-run submissions. It is computed identically in every language (see the [spec](docs/CHECKSUM.md)).
- **Producer-driven parameters.** Beyond weights, your engine declares the parameters it computes; the
  platform stores, indexes, and exposes them for filtering/ranking **without a schema change on our side**.
  Add a new factor next quarter — just declare it. See [Dynamic parameters](docs/PARAMETERS.md).

For historical portfolio/NAV files used by Portfolio X-ray, use the separate
[`POST /xray/portfolios:bulk`](docs/PORTFOLIO-XRAY-INGESTION.md) contract instead of the daily engine-run
contract.

## A minimal run

```json
{
  "producer": { "engineKey": "vulkan-optimizer", "version": "3.2.1" },
  "run": {
    "runId": "vulkan-2026-06-18",
    "engineKey": "vulkan-optimizer",
    "kind": "optimization",
    "asOf": "2026-06-18",
    "positions": [
      { "ticker": "AAPL", "weight":  4.10 },
      { "ticker": "NVDA", "weight": -3.25 }
    ]
  }
}
```

## The same run, with parameters

Declare each parameter once in `parameterDefs`; attach values per name in `params`:

```json
{
  "producer": { "engineKey": "vulkan-optimizer" },
  "run": {
    "runId": "vulkan-2026-06-18",
    "engineKey": "vulkan-optimizer",
    "kind": "optimization",
    "asOf": "2026-06-18",
    "parameterDefs": [
      { "key": "momentum",   "type": "number",  "unit": "z", "direction": "higher_better", "label": "Momentum" },
      { "key": "conviction", "type": "integer", "range": [1, 10], "direction": "higher_better" },
      { "key": "regime",     "type": "enum",    "values": ["risk_on", "risk_off"] }
    ],
    "positions": [
      { "ticker": "AAPL", "weight":  4.10, "params": { "momentum":  0.73, "conviction": 8, "regime": "risk_on"  } },
      { "ticker": "NVDA", "weight": -3.25, "params": { "momentum": -0.11, "conviction": 3, "regime": "risk_off" } }
    ]
  }
}
```

The platform registers `momentum`, `conviction`, and `regime` (discoverable at `GET /v1/engine-parameters`),
projects them for filtering/ranking, and keeps your raw values verbatim. Next run you can add a fourth
parameter — no coordination required.

## Getting connected

You'll receive from your Auspicia integration contact: a **staging base URL**, a **scoped engine bearer
token** (tied to your `engineKey`), and — if the host is behind Cloudflare Access — a **service token**.
Then follow the [go-live checklist](docs/INTEGRATION-GUIDE.md#going-live--checklist).

## Repository layout

```
docs/         Integration guide, quick start, X-ray ingestion, dynamic-parameter model, checksum spec
schema/       JSON Schema for the envelope + frozen checksum reference vectors
clients/
  csharp/     .NET 8 client library + runnable sample + a vectors-parity test
```

## Support & licence

Questions and token requests: your Auspicia integration contact. This kit is released under the
[Apache 2.0 licence](LICENSE) — use it, fork it, port it to your language of choice.
