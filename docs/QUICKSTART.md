# Quick start

Get from zero to an accepted run in about five minutes. You need two things from your Auspicia contact:

- a **base URL** (a staging URL is issued for onboarding), e.g. `https://staging.auspicia.io/api`
- a **client-scoped API key**, e.g. `ak_live_xxxxxxxx`, with `engine-runs:write` for your `engineKey`

Existing integrations with a legacy `eng_...` engine token can keep using it on daily engine-run routes
during migration. New integrations should use API keys.

The Aviana rebrand changed web-app routes, not the ingest API base path. Use the base URL your contact
provisions for API calls. If you need to open the resulting research/advice workflow in the app, use
`https://app.auspicia.io/#/aviana/report` or `https://app.auspicia.io/#/aviana/flow`; do not publish old
`/#/iris/*` links in new integration runbooks.

> If the host is behind Cloudflare Access, you'll also get a `CF-Access-Client-Id` /
> `CF-Access-Client-Secret` service-token pair to send as headers. See the
> [integration guide](INTEGRATION-GUIDE.md#network-access-cloudflare-access).

---

## 1. Save a run to `run.json`

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

## 2. Dry-run it (validates + prices, writes nothing)

```bash
BASE=https://staging.auspicia.io/api
TOKEN=ak_live_xxxxxxxx

curl -sS -X POST "$BASE/v1/engine-runs:validate" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  --data @run.json | jq
```

```json
{ "valid": true, "positions": 2, "exposure": { "gross": 7.35, "net": 0.85, ... },
  "checksum": "sha256:…", "parameterDefs": 0, "coercionWarnings": [] }
```

The returned `checksum` is what the server computed — handy for confirming your own implementation later.

## 3. Submit it (idempotent — safe to retry)

```bash
curl -sS -X POST "$BASE/v1/engine-runs" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  --data @run.json | jq
```

`201` the first time; run it again and you get `200` with `"deduped": true` — no duplicate created. That's
the whole daily loop: **build → (optionally validate) → submit → retry on failure**.

## 4. Add parameters

Declare them once, attach values per name. This run registers `momentum` and `conviction` and makes them
filterable/rankable in the platform — no change needed on our side:

```json
{
  "producer": { "engineKey": "vulkan-optimizer" },
  "run": {
    "runId": "vulkan-2026-06-19", "engineKey": "vulkan-optimizer",
    "kind": "optimization", "asOf": "2026-06-19",
    "parameterDefs": [
      { "key": "momentum",   "type": "number",  "unit": "z", "direction": "higher_better" },
      { "key": "conviction", "type": "integer", "range": [1, 10] }
    ],
    "positions": [
      { "ticker": "AAPL", "weight":  4.10, "params": { "momentum":  0.73, "conviction": 8 } },
      { "ticker": "NVDA", "weight": -3.25, "params": { "momentum": -0.11, "conviction": 3 } }
    ]
  }
}
```

Confirm what registered:

```bash
curl -sS "$BASE/v1/engine-parameters?engineKey=vulkan-optimizer" | jq
```

See [Dynamic parameters](PARAMETERS.md) for the full model (types, coercion, discovery, type stability).

## 5. Or use the C# client

```bash
cd clients/csharp
export AUSPICIA_API_KEY=ak_live_xxxxxxxx
export AUSPICIA_BASE_URL=https://staging.auspicia.io/api
dotnet run --project Sample
```

The client wraps the envelope, computes the checksum, sets `idempotencyKey`, and retries transient failures
for you. See [clients/csharp/README.md](../clients/csharp/README.md).

## Next

- Full contract, errors, and the **[go-live checklist](INTEGRATION-GUIDE.md#going-live--checklist)** →
  [Integration guide](INTEGRATION-GUIDE.md)
- Loading historical portfolio/NAV CSV files for X-ray analysis? Use
  [Portfolio X-ray ingestion](PORTFOLIO-XRAY-INGESTION.md). Multi-org loaders can discover allowed
  targets with `GET /orgs/ingestion-targets` and send top-level `targetOrgId`.
- Need endpoint scopes or key-handling rules? See
  [Client-scoped API keys](API-KEYS.md).
- Verify your checksum against the frozen **[reference vectors](../schema/checksum-test-vectors.json)** →
  [Checksum spec](CHECKSUM.md)
