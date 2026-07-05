# Portfolio X-ray Ingestion API

**Audience:** integration partners and operators loading historical portfolio allocation/performance
files for Auspicia Portfolio X-ray. This is separate from the daily optimizer-run API:

- Use `POST /v1/engine-runs` for daily compute-engine signals.
- Use `POST /xray/portfolios:bulk` for historical portfolio X-ray imports.

Portfolio X-ray ingestion stores uploaded portfolios, validates the file shape, and returns a parse report.
Analysis is started separately so ingestion can be retried, reviewed, or batched without triggering long-running
research jobs by accident.

---

## 1. Endpoint

| | |
|---|---|
| **Base URL** | `https://app.auspicia.io/api` or the staging URL issued for your integration |
| **Bulk import** | `POST /xray/portfolios:bulk` |
| **Target discovery** | `GET /orgs/ingestion-targets` |
| **Content type** | `application/json` |
| **Auth** | Client-scoped API key with `orgs:read` and `xray:write` |
| **Bulk size** | Up to 250 portfolios per request |
| **Partial success** | Yes. Good items commit; malformed items return per-item errors. |

> Auth uses client-scoped API keys; legacy `eng_` engine tokens do not work on X-ray routes. Include any
> network-access headers your Auspicia contact provisions (e.g. Cloudflare Access).

---

## 2. Request Shape

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

You may also send a single portfolio object without wrapping it in `portfolios`; the server treats it as a
one-item bulk request. In that shape, `targetOrgId` is still top-level and is stripped before the item is
parsed.

### Bulk Request Fields

| Field | Type | Req | Notes |
|---|---|---|---|
| `portfolios` / `items` | array | conditional | One or more portfolio objects. Omit only when sending a single portfolio object as the body. |
| `targetOrgId` | string | — | Optional organization id to own the imported portfolios, for example `lampshade`. Must be top-level. |

### Portfolio Fields

| Field | Type | Req | Notes |
|---|---|---|---|
| `name` | string | — | Human-readable portfolio name. Defaults to `Imported portfolio N`. |
| `source` | enum | — | `desk` or `advise`; defaults to `desk`. |
| `allocationsCsv` | string | conditional | Historical allocation CSV. Required if `performanceCsv` is absent. |
| `performanceCsv` | string | conditional | Historical NAV/performance CSV. Required if `allocationsCsv` is absent. |
| `investorPortfolioId` | string | — | Optional external/customer portfolio id for reconciliation. |

At least one of `allocationsCsv` or `performanceCsv` must be present.

### Organization Targeting

The owning organization is resolved server-side from the authenticated API key:

- Omit `targetOrgId` to use the identity's default ingestion organization.
- Pass top-level `targetOrgId` when the same operator identity may ingest for more than one org.
- For client-scoped API keys, use one key per client organization. A LampShade key defaults to
  `lampshade` and can only send `"targetOrgId": "lampshade"`.
- Do not put `targetOrgId` inside individual `portfolios[]` items; per-item org targeting returns an
  item-level `422`.
- Unknown/inactive target orgs return request-level `404`.
- Known orgs that your identity is not allowed to ingest for return request-level `403`.

Discover allowed targets before submitting:

```bash
curl -sS "$BASE/orgs/ingestion-targets" \
  -H "Authorization: Bearer $AUSPICIA_API_KEY" \
  | jq
```

Example response:

```json
{
  "orgs": [
    { "id": "lampshade", "displayName": "LampShade", "status": "active", "role": "api-key" }
  ],
  "defaultOrgId": "lampshade"
}
```

---

## 3. CSV Contracts

### Allocations CSV

The first column must be `Date`; remaining columns are ticker symbols. `Cash` is supported as a special
column. Weights are decimal fractions of capital, so `0.05` means 5%. Blanks and zeros are dropped.

Supported date formats:

- `DD/MM/YYYY`
- `YYYY-MM-DD`
- `MM/DD/YYYY`

Example:

```csv
Date,AAPL,MSFT,NVDA,Cash
01/04/2016,0.30,0.25,0.10,0.35
04/04/2016,0.32,0.24,0.11,0.33
```

### Performance CSV

The first column must be `Date`. NAV can be supplied as `PortfolioValue` or `NAV`.

Optional columns:

- `DailyReturnPct`
- `NumLongs`
- `NumShorts`
- `GrossExposurePct`
- `TurnoverPct`

Example:

```csv
Date,PortfolioValue,DailyReturnPct,NumLongs,NumShorts,GrossExposurePct,TurnoverPct
01/04/2016,1000000,,3,0,65,0
04/04/2016,1003500,0.35,3,0,67,1.2
```

When a performance file is present, Portfolio X-ray uses its NAV series for drawdown episode detection.
Allocation-only imports can still be analyzed, but NAV must be reconstructed from lagged weights and daily
security returns.

---

## 4. Response

If every item succeeds, the endpoint returns `201 Created`. If some items fail and some succeed, it returns
`207 Multi-Status`. If every item is malformed, the endpoint still returns `207 Multi-Status` with an empty
`imported` array and per-item errors, so callers can repair and replay individual items.

```json
{
  "imported": [
    {
      "index": 0,
      "portfolio": {
        "id": "xray_3b8f...",
        "orgId": "auspicia",
        "createdBy": "operator@example.com",
        "name": "LampShade 10-year model portfolio",
        "source": "desk",
        "startDate": "2016-04-01",
        "endDate": "2026-03-30",
        "hasAllocations": true,
        "hasPerformance": true,
        "investorPortfolioId": "optional-external-id",
        "createdAt": "2026-07-04T14:10:00Z"
      },
      "parseReport": {
        "allocRows": 151541,
        "perfRows": 2512,
        "dateRange": { "start": "2016-04-01", "end": "2026-03-30" },
        "tickers": {
          "known": ["AAPL", "MSFT"],
          "unknown": ["SPY"]
        },
        "warnings": ["1 ticker(s) have no curated sector mapping and will use Unknown."],
        "grossMax": 3.000009,
        "cashColumn": true
      }
    }
  ],
  "errors": [
    { "index": 1, "status": 422, "detail": "Upload allocations and/or performance CSV." }
  ],
  "count": 1,
  "failed": 1
}
```

Unknown tickers are reported, not rejected. They can still be imported; attribution later reports them in
data-quality warnings or the `Unknown` sector bucket.

### Error Handling

Request-level errors stop the whole request:

| Status | Meaning | Caller action |
|---:|---|---|
| `401` | Authentication or network-access identity failed | Refresh/provision the API key or Access headers. |
| `403` | The identity is not authorized for `targetOrgId` | Use a returned target from `GET /orgs/ingestion-targets` or request access. |
| `404` | `targetOrgId` is unknown or inactive | Correct the target org id or request org provisioning. |
| `413` | More than 250 portfolios in one request | Split into smaller batches. |
| `422` | Missing or empty `portfolios`/`items` array | Fix request shape. |
| `503` | Database unavailable or service not configured | Retry with backoff; escalate if persistent. |

Item-level errors are returned inside `errors[]` with `{index, status, detail}` and do not roll back other
valid items in the same request. Common item statuses:

| Status | Meaning | Caller action |
|---:|---|---|
| `400` | CSV shape is unusable, invalid `source`, bad date, missing NAV column, etc. | Fix that item and retry it. |
| `422` | Item is not an object, has neither allocations nor performance CSV, or contains per-item `targetOrgId`. | Fix that item and retry it. |
| `500` | Unexpected item-level failure after other items may have committed. | Retry that item after checking logs. |

On `207 Multi-Status`, retry only the failed item indexes after fixing their payloads.

---

## 5. Starting Analysis

Ingestion does not automatically run analysis. After a portfolio imports successfully:

```bash
# Add any service-auth headers your Auspicia contact provided, for example
# Cloudflare Access service-token headers on protected staging hosts.
curl -sS -X POST "$BASE/xray/portfolios/$PORTFOLIO_ID/analyses" \
  -H "Content-Type: application/json" \
  --data '{ "thresholdPct": 10, "topN": 8 }' | jq
```

Client-scoped API keys currently cover X-ray ingestion, not X-ray reads or analysis control. `xray:read` is
not exposed yet, so the analysis and polling calls above require the app/operator identity your Auspicia
contact provides.

`topN` defaults to `8`. Pass a higher value for a longer table, or omit `topN` for the standard UI-sized
episode set. Internal forensic tooling may request the full uncapped list, but partner integrations should
prefer a finite `topN`.

Response:

```json
{
  "analysisId": "xray_analysis_9f2c...",
  "analysis": {
    "id": "xray_analysis_9f2c...",
    "portfolioId": "xray_3b8f...",
    "status": "queued",
    "stage": "queued",
    "message": "Queued Portfolio X-ray analysis."
  }
}
```

Poll:

- `GET /xray/analyses/{analysisId}` for status, events, and completed episodes.
- `GET /xray/analyses/active?portfolioId={portfolioId}` for the active queued/running analysis; returns
  a bare analysis object or `null`.
- `GET /xray/portfolios/{portfolioId}/nav` for the imported NAV series.

Completed episodes use 0-based `idx` values. Each episode also includes `kind`:

- `primary` — high-watermark drawdown spell.
- `nested` — local event-shaped drawdown inside a longer unrecovered spell.

Narratives are generated on demand:

```bash
# Add any service-auth headers your Auspicia contact provided.
curl -sS -X POST "$BASE/xray/analyses/$ANALYSIS_ID/episodes/0/narrative" \
  -H "Content-Type: application/json" \
  --data '{ "force": false }' | jq
```

---

## 6. Related Daily Import Endpoints

Operator/admin daily import routes use the same organization targeting convention:

- `POST /imports/daily`
- `POST /imports/daily/jobs`

Both accept top-level `targetOrgId` next to the existing `portfolio`, `run`, `researchLimit`, and `force`
fields. These routes run the import → research → advice workflow; use the X-ray bulk endpoint when you only
need to load historical allocation/NAV CSVs. Service integrations calling these routes need an API key with
`imports:daily`; `xray:write` is not enough.

```json
{
  "targetOrgId": "lampshade",
  "portfolio": { "id": "lamp-model", "name": "LampShade model", "holdings": [] },
  "run": { "date": "2026-07-04", "names": [] },
  "researchLimit": 5,
  "force": false
}
```

---

## 7. curl Example

```bash
BASE=https://staging.auspicia.io/api
API_KEY=ak_live_xxxxx

# If your staging host is protected by Cloudflare Access, include:
#   -H "CF-Access-Client-Id: $CF_ACCESS_CLIENT_ID"
#   -H "CF-Access-Client-Secret: $CF_ACCESS_CLIENT_SECRET"

jq -n \
  --arg name "LampShade historical portfolio" \
  --arg targetOrgId "lampshade" \
  --rawfile allocationsCsv PortfolioAllocations.csv \
  --rawfile performanceCsv PortfolioPerformance.csv \
  '{ targetOrgId: $targetOrgId, portfolios: [{ name: $name, source: "desk", allocationsCsv: $allocationsCsv, performanceCsv: $performanceCsv }] }' \
| curl -sS -X POST "$BASE/xray/portfolios:bulk" \
    -H "Authorization: Bearer $API_KEY" \
    -H "Content-Type: application/json" \
    --data @- \
| jq
```

### C# Helper

The C# client includes `AuspiciaXrayClient`, which wraps this same endpoint and returns `XrayBulkImportResult`
for both `201 Created` and `207 Multi-Status`. It exposes per-item failures as `Errors[]` and throws typed
exceptions for request-level failures:

- `XrayRequestException` for `400`, `413`, and request-level `422`.
- `XrayAuthException` for `401` / `403`.
- `XrayIngestException` for exhausted `5xx`, network, or timeout failures.

See [clients/csharp](../clients/csharp/) for usage.

---

## 8. Operational Notes

- Keep each bulk request below 250 portfolios.
- Use stable `name` / `investorPortfolioId` values so operators can reconcile imports.
- Discover allowed organizations with `GET /orgs/ingestion-targets` using a key with `orgs:read`; submit one
  top-level `targetOrgId` for the whole bulk request when you need to override the default org.
- Use performance CSV whenever you have a trusted NAV series; it avoids reconstructing NAV from prices.
- Analysis attribution may require market-data coverage. Import can succeed even when later attribution has
  missing-price warnings.
- On `207 Multi-Status`, retry only the failed items after fixing their CSV payloads.
