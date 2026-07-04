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
| **Content type** | `application/json` |
| **Auth** | Authenticated Auspicia API identity / service identity issued for your integration |
| **Bulk size** | Up to 250 portfolios per request |
| **Partial success** | Yes. Good items commit; malformed items return per-item errors. |

> Note: this endpoint is not an engine-token endpoint. Engine bearer tokens are scoped to `/v1/engine-runs`.
> For machine-to-machine X-ray ingestion, use the authenticated service identity and network-access headers
> your Auspicia contact provisions.

---

## 2. Request Shape

```json
{
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
one-item bulk request.

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

Current production behavior derives the owning organization from the authenticated API identity. A
`targetOrgId` field is planned for multi-organization operators; do not rely on it until your Auspicia
contact confirms it is enabled for your tenant.

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
| `401` / `403` | Authentication or network-access identity failed | Refresh/provision service identity. |
| `413` | More than 250 portfolios in one request | Split into smaller batches. |
| `422` | Missing or empty `portfolios`/`items` array | Fix request shape. |
| `503` | Database unavailable or service not configured | Retry with backoff; escalate if persistent. |

Item-level errors are returned inside `errors[]` with `{index, status, detail}` and do not roll back other
valid items in the same request. Common item statuses:

| Status | Meaning | Caller action |
|---:|---|---|
| `400` | CSV shape is unusable, invalid `source`, bad date, missing NAV column, etc. | Fix that item and retry it. |
| `422` | Item is not an object or has neither allocations nor performance CSV. | Fix that item and retry it. |
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

## 6. curl Example

```bash
BASE=https://staging.auspicia.io/api

# If your staging host is protected by Cloudflare Access, include:
#   -H "CF-Access-Client-Id: $CF_ACCESS_CLIENT_ID"
#   -H "CF-Access-Client-Secret: $CF_ACCESS_CLIENT_SECRET"

alloc_csv=$(python3 - <<'PY'
from pathlib import Path
print(Path("PortfolioAllocations.csv").read_text())
PY
)

perf_csv=$(python3 - <<'PY'
from pathlib import Path
print(Path("PortfolioPerformance.csv").read_text())
PY
)

jq -n \
  --arg name "LampShade historical portfolio" \
  --arg allocationsCsv "$alloc_csv" \
  --arg performanceCsv "$perf_csv" \
  '{ portfolios: [{ name: $name, source: "desk", allocationsCsv: $allocationsCsv, performanceCsv: $performanceCsv }] }' \
| curl -sS -X POST "$BASE/xray/portfolios:bulk" \
    -H "Content-Type: application/json" \
    --data @- \
| jq
```

If your API host is protected by Cloudflare Access or another service-auth layer, include the headers your
Auspicia contact provided.

### C# Helper

The C# client includes `AuspiciaXrayClient`, which wraps this same endpoint and returns `XrayBulkImportResult`
for both `201 Created` and `207 Multi-Status`. It exposes per-item failures as `Errors[]` and throws typed
exceptions for request-level failures:

- `XrayRequestException` for `400`, `413`, and request-level `422`.
- `XrayAuthException` for `401` / `403`.
- `XrayIngestException` for exhausted `5xx`, network, or timeout failures.

See [clients/csharp](../clients/csharp/) for usage.

---

## 7. Operational Notes

- Keep each bulk request below 250 portfolios.
- Use stable `name` / `investorPortfolioId` values so operators can reconcile imports.
- Use performance CSV whenever you have a trusted NAV series; it avoids reconstructing NAV from prices.
- Analysis attribution may require market-data coverage. Import can succeed even when later attribution has
  missing-price warnings.
- On `207 Multi-Status`, retry only the failed items after fixing their CSV payloads.
