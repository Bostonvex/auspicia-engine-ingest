# Auspicia — Engine Ingestion API: Integration Guide

**Audience:** an engine platform (a GPU/CPU optimizer or any compute engine) integrating to push daily runs
into Auspicia. **Version:** schema `1.0`. **Transport:** HTTPS + JSON.

This is the machine-to-machine API for submitting an optimizer run — a vector of signed per-name weights
for one trading day, plus any parameters your engine computes. The [C# client](../clients/csharp/)
implements everything below; use it directly or follow this guide for any language. New to the API? Start
with the [quick start](QUICKSTART.md).

---

## 1. At a glance

| | |
|---|---|
| **Base URL** | `https://app.auspicia.io/api` (provisioned per-integration; a staging URL is issued for testing) |
| **Engine auth** | `Authorization: Bearer <engine-token>` — a scoped token we issue you, tied to your `engineKey` |
| **Submit a run** | `POST /v1/engine-runs` (JSON) or `POST /v1/engine-runs:csv` (CSV) |
| **Import historical portfolios** | `POST /xray/portfolios:bulk` (separate Portfolio X-ray contract) |
| **X-ray auth** | Authenticated Auspicia API/service identity; see the X-ray guide for details |
| **Test without writing** | `POST /v1/engine-runs:validate` (dry-run — validate + price, no persistence) |
| **Discover parameters** | `GET /v1/engine-parameters` |
| **Idempotent** | Yes — safe to retry; re-submitting the same `idempotencyKey` returns the stored run, `deduped: true` |
| **Errors** | RFC 7807 `application/problem+json` on 4xx; retryable on 5xx |
| **One run per day** | The newest accepted run for an `(engineKey, asOf)` wins; the prior is marked `superseded` |

**Recommended flow:** `validate` once during onboarding → `submit` daily → on network error, **retry the
same envelope** (idempotency makes this safe).

Need to load historical allocation/NAV files for drawdown attribution instead of a daily optimizer signal?
Use the separate [Portfolio X-ray ingestion guide](PORTFOLIO-XRAY-INGESTION.md).

### Network access (Cloudflare Access)

The API host may sit behind **Cloudflare Access**. In addition to your engine bearer token, your machine
then passes Access using a **Cloudflare Access service token** — we provision one and give you a
`CF-Access-Client-Id` + `CF-Access-Client-Secret` pair to send as headers on every request. (Alternatively
we issue a dedicated ingest hostname that bypasses Access and relies on the bearer token alone — we'll tell
you which applies.) The reference client accepts these via a preconfigured `HttpClient`.

---

## 2. Authentication

Every request carries a bearer token we issue you:

```
Authorization: Bearer eng_live_xxxxxxxxxxxxxxxxxxxxxxxx
```

- The token is **scoped to your `engineKey`** (e.g. `vulkan-optimizer`). Submitting a run whose
  `engineKey` doesn't match your token → `403`.
- Tokens are compared in constant time. Keep them secret (env var / secrets manager); rotate on request.
- Missing/malformed token → `401`. If ingestion isn't yet enabled for your account → `503`.

---

## 3. The run envelope

A submission is an **envelope** wrapping one **run**. Minimal valid example:

```json
{
  "schemaVersion": "1.0",
  "idempotencyKey": "vulkan-2026-06-18",
  "producer": { "engineKey": "vulkan-optimizer", "version": "3.2.1" },
  "emittedAt": "2026-06-18T21:05:00Z",
  "checksum": "sha256:9f2c…",
  "run": {
    "runId": "vulkan-2026-06-18",
    "engineKey": "vulkan-optimizer",
    "kind": "optimization",
    "economics": "implementation",
    "modality": "gpu-batch",
    "asOf": "2026-06-18",
    "universe": "US-SP500",
    "ccy": "USD",
    "substrate": "Vulkan 1.3 · SPIR-V · NVIDIA H100",
    "solveMs": 412,
    "positions": [
      { "ticker": "AAPL", "weight": 4.10, "score": 0.62, "rank": 1 },
      { "ticker": "NVDA", "weight": -3.25, "score": -0.48, "rank": 2 }
    ]
  }
}
```

A machine-readable [JSON Schema](../schema/envelope.schema.json) is provided — validate your payloads in CI.

### Envelope fields

| Field | Type | Req | Notes |
|---|---|---|---|
| `schemaVersion` | string | — | defaults to `"1.0"` (the only supported version) |
| `idempotencyKey` | string | — | your dedup key; **defaults to `run.runId`** if omitted. See §6. |
| `producer.engineKey` | string | ✔ | must equal `run.engineKey` |
| `producer.version` | string | — | your engine build/version (for our logs) |
| `emittedAt` | ISO-8601 datetime | — | when you emitted the envelope |
| `checksum` | string | — | optional integrity hash; if present it **must** match ([spec](CHECKSUM.md)) |
| `run` | object | ✔ | the run (below) |

### Run fields

| Field | Type | Req | Notes |
|---|---|---|---|
| `runId` | string | ✔ | your stable id for this run |
| `engineKey` | string | ✔ | your engine key (matches the token scope) |
| `kind` | enum | ✔ | `optimization` \| `research` |
| `economics` | enum | — | `implementation` \| `directional` |
| `modality` | enum | — | `gpu-batch` \| `gpu-rt` \| `streaming` \| `cpu` |
| `asOf` | date `YYYY-MM-DD` | ✔ | the signal / trading day |
| `universe` | string | — | e.g. `US-SP500` |
| `ccy` | enum | — | `USD` (default) \| `ZAR` |
| `substrate` | string | — | free-text provenance |
| `solveMs` | int ≥ 0 | — | solve time |
| `attributesPerName` | int ≥ 0 | — | point-cloud depth |
| `parameterDefs` | array | — | declares per-name parameters (§5). Omit for a weights-only run. |
| `positions` | array | ✔ | 1–5000 rows (below) |

### Position fields

| Field | Type | Req | Notes |
|---|---|---|---|
| `ticker` | string | ✔ | symbol; upper-cased on ingest |
| `weight` | number | ✔ | **signed** % of capital (`+` long, `−` short). Finite, `|weight| ≤ 100`. |
| `side` | enum | — | `long` \| `short` — if given, must match the sign of `weight` |
| `score` | number | — | −1..1 normalized signal (derived from weight if omitted) |
| `rank` | int ≥ 0 | — | conviction rank (defaults to array order) |
| `figi` / `cik` | string | — | security identifiers (help us map new names) |
| `attributes` | object | — | freeform per-name payload (stored verbatim, not typed/indexed) |
| `params` | object | — | **declared** per-name parameter values (§5). Every key must appear in `parameterDefs`. |

**Validation rules (else `422`):** no duplicate tickers within a run; every `weight` finite and
`|weight| ≤ 100`; total gross `Σ|weight| ≤ 500`; `score ∈ [−1, 1]`; every `params` key declared in
`parameterDefs`. These reject malformed feeds, not legitimate books.

---

## 4. Endpoints

### `POST /v1/engine-runs` — submit (JSON)
Body = the envelope. Success:
- `201 Created` — new run accepted.
- `200 OK` with `"deduped": true` — this `idempotencyKey` was already ingested (safe replay).

```json
{
  "id": "8f3c…-uuid",
  "runId": "vulkan-2026-06-18",
  "engineKey": "vulkan-optimizer",
  "status": "accepted",
  "deduped": false,
  "positions": 2,
  "asOf": "2026-06-18",
  "exposure": { "gross": 7.35, "net": 0.85, "longPct": 4.10, "shortPct": 3.25 },
  "supersedes": ["<uuid of the run this one replaced>"],
  "parametersRegistered": ["momentum", "conviction"],
  "coercionWarnings": []
}
```

### `POST /v1/engine-runs:validate` — dry-run (no write)
Same body; validates + prices the envelope **without persisting** (no dead-letter, no supersede, no
registration). Use it in onboarding/CI to confirm shape, auth, checksum, and coercions. Returns `200`:
```json
{ "valid": true, "runId": "…", "engineKey": "…", "asOf": "2026-06-18", "kind": "optimization",
  "positions": 2, "exposure": { "gross": 7.35, "net": 0.85, "longPct": 4.10, "shortPct": 3.25 },
  "checksum": "sha256:…", "schemaVersion": "1.0", "parameterDefs": 2, "coercionWarnings": [] }
```
The returned `checksum` is the value we compute — diff it against your own to confirm your implementation.

### `GET /v1/engine-parameters` — discover the registry (no auth)
Every parameter your engine has declared, so you can confirm registration and so the platform UI can build
filters. Optional `?engineKey=…` scope.
```json
{ "parameters": [
  { "engineKey": "vulkan-optimizer", "key": "momentum", "type": "number", "unit": "z",
    "direction": "higher_better", "label": "Momentum", "range": null, "values": null,
    "schemaVersion": "1.0", "firstSeenAt": "2026-06-18T21:05:03Z" }
] }
```

### `POST /v1/engine-runs:csv` — submit (CSV)
For the legacy `TradeWeights` shape (weights only — no parameters). Query:
`?engineKey=vulkan-optimizer&asOf=2026-06-18`. Body is CSV:
```
date,AAPL,NVDA,XOM,…
2026-06-18,4.10,-3.25,0,…
```
First column = date; remaining columns = signed weights. Zero/blank cells are dropped. Same response as JSON.

### Reads (no auth required)
- `GET /v1/engine-runs?engineKey=…&asOf=YYYY-MM-DD&limit=20` — recent run summaries.
- `GET /v1/engine-runs/{id|runId}` — one run with all positions (incl. their `params`).
- `GET /v1/securities/{symbol}/optimizer/latest` — the latest accepted weight for a name.

### Portfolio X-ray bulk ingestion

Historical portfolio X-ray imports use a separate endpoint because they carry CSV allocation/NAV history,
not daily optimizer signals:

- `POST /xray/portfolios:bulk`
- JSON body: `{ "portfolios": [{ "name", "source", "allocationsCsv", "performanceCsv", "investorPortfolioId" }] }`
- Returns `201 Created` when all items import, or `207 Multi-Status` for partial success.
- Analysis is started separately with `POST /xray/portfolios/{portfolioId}/analyses`.

See [Portfolio X-ray ingestion](PORTFOLIO-XRAY-INGESTION.md) for the full request/response contract.

---

## 5. Parameters (producer-driven)

Beyond weights, declare the parameters your engine computes and attach values per name. The platform
registers, projects (for filter/rank), and stores them — **with no schema change on our side**, and the set
can grow every run. The full model — types, coercion, type-stability, discovery — is in
**[Dynamic parameters](PARAMETERS.md)**. In brief:

```json
"parameterDefs": [
  { "key": "momentum", "type": "number", "unit": "z", "direction": "higher_better" },
  { "key": "regime",   "type": "enum",   "values": ["risk_on", "risk_off"] }
],
"positions": [
  { "ticker": "AAPL", "weight": 4.10, "params": { "momentum": 0.73, "regime": "risk_on" } }
]
```

- **Types:** `number`, `integer`, `boolean`, `string`, `enum`, `vector`, `json`.
- **Undeclared value → `422`.** Every `params` key must be declared in the same run.
- **Type conflict → `422`.** A parameter's `type` is fixed within a `schemaVersion` (re-type via a new key).
- **Type mismatch → flagged, not dropped.** A `"n/a"` for a `number` is kept and reported in
  `coercionWarnings` (run `:validate` to preview). The value is excluded from type-aware numeric filters.
- **Parameters are covered by the checksum.** See [CHECKSUM.md](CHECKSUM.md); a run with no parameters hashes
  exactly as before (fully backward-compatible).

---

## 6. Idempotency & retries

Ingest is **idempotent on `(engineKey, idempotencyKey)`**. If you don't set `idempotencyKey`, it defaults
to `runId`.

- Re-submitting the same key returns the **stored** run with `"deduped": true` (`200`) — it does **not**
  create a duplicate or re-supersede anything.
- This is **race-safe**: two concurrent submissions of the same key both succeed (one `201`, one `200`).
- **On any network failure or timeout, just retry the identical envelope.** That is the intended pattern —
  never construct a new key for a retry.
- To publish a *corrected* run for a day (different weights or parameter **values**), submit a **new**
  `idempotencyKey` (e.g. `…-r2`). It becomes the accepted run; the prior is `superseded`.
- A **new parameter declaration** in `parameterDefs` is registered even if the run body itself dedups — so a
  retry that adds a parameter never silently loses the declaration.

---

## 7. Integrity checksum (optional but recommended)

Set `checksum` to detect in-flight corruption / wrong-run submissions. If present, it must match or the run
is rejected (`422`). The algorithm is language-stable (fixed 6-dp number formatting + canonical
percent-encoding), and **fully specified** — with a worked example and frozen reference vectors — in
**[CHECKSUM.md](CHECKSUM.md)**. The C# client computes it for you; `:validate` echoes back our value so you
can diff.

---

## 8. Errors

**4xx** are terminal (fix and re-send); **5xx** are transient (retry with backoff).

| Status | Meaning | Action |
|---|---|---|
| `200` | deduped / dry-run ok | none |
| `201` | accepted | none |
| `401` | missing/invalid token | fix auth |
| `403` | token scoped to a different `engineKey` | fix engineKey/token |
| `422` | invalid envelope (validation / checksum / undeclared param / type conflict) | fix payload — **do not retry unchanged** |
| `503` | ingestion not configured / DB unavailable | retry with backoff |

Validation failures return **RFC 7807** `application/problem+json`:
```json
{
  "type": "https://auspicia.local/problems/invalid-engine-run",
  "title": "Invalid engine run",
  "status": 422,
  "detail": "positions carry params not declared in parameterDefs: sharpe",
  "errors": [ { "path": "run.positions.1.weight", "msg": "weight must be finite" } ],
  "deadletterId": "e1c2…"
}
```
A rejected **live** submission is captured in a **dead-letter** queue (`deadletterId`) so we can inspect and
replay it operator-side after you fix the root cause. (Dry-run `:validate` failures are **not**
dead-lettered.)

---

## 9. Semantics you can rely on

- **Append-only:** accepted runs are immutable; corrections supersede (never mutate).
- **One accepted run per day** per `engineKey` — the newest wins, the rest become `superseded`.
- **Server-derived exposure:** we compute `gross/net/longPct/shortPct` from your weights; you don't send them.
- **`score`** defaults to `clamp(weight / 3.5, −1, 1)` if you omit it.
- **Parameters are additive:** they never change the behaviour or checksum of a weights-only run.

---

## 10. curl quickstart

```bash
BASE=https://app.auspicia.io/api
TOKEN=eng_live_xxxxx

# 1) dry-run
curl -sS -X POST "$BASE/v1/engine-runs:validate" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  --data @run.json | jq

# 2) submit (idempotent — safe to retry on failure)
curl -sS -X POST "$BASE/v1/engine-runs" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  --data @run.json | jq

# 3) confirm what registered
curl -sS "$BASE/v1/engine-parameters?engineKey=vulkan-optimizer" | jq
```

---

## 11. Going live — checklist

1. We issue you a **staging base URL + token**; you `:validate` a real run and confirm `valid: true`.
2. Confirm your **checksum** matches the `checksum` returned by `:validate` (and the
   [reference vectors](../schema/checksum-test-vectors.json)).
3. If you send parameters: declare them, `:validate`, and confirm `coercionWarnings` is empty and
   `GET /v1/engine-parameters` shows the expected types.
4. Submit to **staging** `/v1/engine-runs`; confirm `201` then a retry returns `200 deduped`.
5. We issue the **production** token; switch the base URL. Schedule your daily push (idempotent, so a cron
   that retries on failure is safe).
6. Alert on any non-`2xx`. `422` → your bug (check `detail`); `5xx` → retry, then page us if persistent.

Questions / token requests: your Auspicia integration contact. The reference client is in
[`clients/csharp/`](../clients/csharp/README.md).
