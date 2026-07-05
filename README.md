# Auspicia Integration Kit

**Machine-to-machine contracts for getting quantitative signals and historical portfolios into Auspicia.**

This repository covers two separate ingestion paths:

| Use case | Endpoint | Use it when |
|---|---|---|
| Daily optimizer / compute-engine signals | `POST /v1/engine-runs` | Your engine emits one current run for a trading day: signed per-name weights plus optional parameters such as momentum, quality, conviction, risk, embeddings, etc. |
| Historical portfolio X-ray imports | `POST /xray/portfolios:bulk` | You need to bulk-load allocation and NAV/performance CSV history so Auspicia can run drawdown detection, attribution, and Portfolio X-ray analysis. |

The two paths intentionally have different auth, payloads, idempotency, and error semantics. Start with the
guide that matches what you are loading:

| | |
|---|---|
| **[Quick start](docs/QUICKSTART.md)** | Submit your first daily engine run in about 5 minutes (curl + C#). |
| **[Engine integration guide](docs/INTEGRATION-GUIDE.md)** | Complete daily-run contract: auth, envelope, endpoints, idempotency, errors, go-live. |
| **[Portfolio X-ray ingestion](docs/PORTFOLIO-XRAY-INGESTION.md)** | Bulk-load historical allocation/NAV CSVs for X-ray drawdown attribution. |
| **[Client-scoped API keys](docs/API-KEYS.md)** | Auth model, org binding, endpoint scopes, and key-handling rules. |
| **[Dynamic parameters](docs/PARAMETERS.md)** | Declare arbitrary per-name parameters the platform consumes by definition. |
| **[Checksum spec](docs/CHECKSUM.md)** | Language-stable integrity algorithm for daily engine runs. |
| **[JSON Schema](schema/envelope.schema.json)** | Machine-readable daily engine-run envelope contract. |
| **[Reference vectors](schema/checksum-test-vectors.json)** | Frozen checksum test cases every client must reproduce byte-for-byte. |
| **[C# client](clients/csharp/)** | .NET 8 client for daily engine-run submission and X-ray bulk import. |

---

## Daily Engine Runs

Use this path when your engine emits one **run** per trading day. You wrap it in an **envelope** and `POST`
it over HTTPS:

```
     your engine                                          Auspicia
┌───────────────────┐   POST /v1/engine-runs   ┌─────────────────────────────┐
│ runId, asOf,      │ ────────────────────────▶│ validate → checksum → store │
│ positions[],      │     Bearer <api-key>     │ supersede the prior day     │
│ parameterDefs[]   │ ◀────────────────────────│ register parameters         │
└───────────────────┘  201 accepted · 200 dedup└─────────────────────────────┘
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

### Minimal daily run

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

### The same run, with parameters

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

## Portfolio X-ray Historical Imports

Use this path when you are loading allocation and NAV/performance history, not a daily optimizer signal.
Portfolio X-ray ingestion accepts JSON containing one or more portfolio CSV pairs:

```json
{
  "targetOrgId": "lampshade",
  "portfolios": [
    {
      "name": "LampShade 10-year model portfolio",
      "source": "desk",
      "allocationsCsv": "Date,AAPL,MSFT,Cash\n01/04/2016,0.45,0.40,0.15\n",
      "performanceCsv": "Date,PortfolioValue,DailyReturnPct\n01/04/2016,1000000,\n04/04/2016,1003500,0.35\n",
      "investorPortfolioId": "optional-external-id"
    }
  ]
}
```

`targetOrgId` is optional (it defaults to the key's organization) and must be top-level when present.
Discover the organizations a key may ingest for with `GET /orgs/ingestion-targets`. The full targeting
rules are in [Client-scoped API keys](docs/API-KEYS.md) and the X-ray guide.

`POST /xray/portfolios:bulk` returns:

- `201 Created` when every item imports.
- `207 Multi-Status` when some or all items fail item-level validation.
- Per-item failures as `{index, status, detail}`; retry only failed indexes after repair.
- A `parseReport` with row counts, date range, known/unknown tickers, gross exposure max, cash-column
  detection, and warnings.

Analysis is a separate step: `POST /xray/portfolios/{portfolioId}/analyses`. The default analysis returns a
finite `topN=8` episode set for the UI. Completed episodes use 0-based `idx` values and may include
`kind: "primary" | "nested"` so consumers can distinguish high-watermark drawdowns from nested event-shaped
drawdowns.

Full contract: [Portfolio X-ray ingestion](docs/PORTFOLIO-XRAY-INGESTION.md).

## Getting connected

You will receive from your Auspicia integration contact:

- a staging base URL, for example `https://staging.auspicia.io/api`
- one or more client-scoped API keys:
  - use one key per client organization, for example one LampShade key for `targetOrgId=lampshade`
  - daily engine-run keys need `engine-runs:write` or `engine-runs:validate` and matching `engineKeys`
  - Portfolio X-ray keys need `orgs:read` plus `xray:write`
  - daily import keys need `imports:daily`
- Cloudflare Access service-token headers if the host is protected by Access

The Iris-to-Aviana rebrand affects human-facing app links, not the machine-to-machine API paths in this
kit. Continue to use the provisioned API base URL for `POST /v1/engine-runs`, X-ray bulk import, and daily
imports. When an operator needs to inspect the resulting research/advice workflow in the web app, use
Aviana routes such as `https://app.auspicia.io/#/aviana/report` or `https://app.auspicia.io/#/aviana/flow`.
Legacy `/#/iris/*` bookmarks currently redirect, but new integration material should not publish Iris URLs.

Legacy `eng_...` engine tokens remain supported for daily engine-run routes during migration, but new
integrations should use API keys. See [Client-scoped API keys](docs/API-KEYS.md) for the complete scope
table and key-handling rules.

For daily engine runs, follow the [go-live checklist](docs/INTEGRATION-GUIDE.md#going-live--checklist).
For Portfolio X-ray, start with the [X-ray operational notes](docs/PORTFOLIO-XRAY-INGESTION.md#8-operational-notes).

## Repository layout

```
docs/         Integration guide, quick start, X-ray ingestion, dynamic-parameter model, checksum spec
schema/       JSON Schema for the envelope + frozen checksum reference vectors
clients/
  csharp/     .NET 8 client library + runnable sample + a vectors-parity test
```

## What this repo is not

- It is not a trading/order API.
- It is not the Auspicia application source tree.
- The C# client includes helpers for daily engine-run submission and Portfolio X-ray bulk import. It does
  not place trades or call broker/order APIs.

## Support & licence

Questions and API-key requests: your Auspicia integration contact. This kit is released under the
[Apache 2.0 licence](LICENSE) — use it, fork it, port it to your language of choice.
