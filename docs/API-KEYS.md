# Client-scoped API keys

Auspicia service API keys are bearer tokens for machine-to-machine ingestion. Each key belongs to exactly
one client organization, carries explicit scopes, and is shown only once when Auspicia creates it. Store the
plaintext key in your secret manager; Auspicia cannot recover it later. If a key is lost, expired, or exposed,
ask your Auspicia contact to revoke it and issue a replacement.

Send the key on every request:

```http
Authorization: Bearer ak_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

If the hostname is protected by Cloudflare Access, also send the service-token headers your Auspicia contact
provided:

```http
CF-Access-Client-Id: <client-id>
CF-Access-Client-Secret: <client-secret>
```

A complete request therefore carries:

```http
Authorization: Bearer ak_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
Content-Type: application/json            # text/csv for POST /v1/engine-runs:csv
CF-Access-Client-Id: <client-id>          # only on Access-protected hosts
CF-Access-Client-Secret: <client-secret>  # only on Access-protected hosts
```

Send the key only as a `Bearer` authorization header — never as `X-API-Key`, a query parameter, a cookie,
or a body field. Cloudflare Access and the API key are independent layers: Access proves the machine may
reach the host; the key proves the caller's organization, scopes, and engine keys. Both are required on
Access-protected hosts.

Legacy engine tokens that start with `eng_` remain supported for daily engine-run routes during migration,
but new integrations should use client-scoped API keys.

## One key per client organization

Use a separate API key for each client org. For example, a service loading LampShade data should use a key
whose organization is `lampshade`.

That key can:

- omit `targetOrgId`, in which case the server writes to `lampshade`;
- send top-level `"targetOrgId": "lampshade"`;
- discover only its own target from `GET /orgs/ingestion-targets`.

That key cannot write to another org. A request with `"targetOrgId": "auspicia"` or any other org returns
`403`.

## Endpoint scopes

| Endpoint | Required scope | Notes |
|---|---|---|
| `GET /orgs/ingestion-targets` | `orgs:read` | API-key callers receive only the key's org and default org id. |
| `POST /xray/portfolios` | `xray:write` | Browser-style X-ray upload. API-key org binding still applies. |
| `POST /xray/portfolios:bulk` | `xray:write` | Historical Portfolio X-ray bulk ingestion. Use one top-level `targetOrgId`; per-item org targeting is rejected. |
| `POST /imports/daily` | `imports:daily` | Runs the daily import -> research -> advice workflow for the key's org. |
| `POST /imports/daily/jobs` | `imports:daily` | Same contract as `/imports/daily`, but starts the background job form. |
| `POST /v1/engine-runs` | `engine-runs:write` | Also requires the key's `engineKeys` allowlist to include `run.engineKey` or `*`. |
| `POST /v1/engine-runs:csv` | `engine-runs:write` | Also requires `engineKeys` to include the query-string `engineKey` or `*`. |
| `POST /v1/deadletter/{id}:replay` | `engine-runs:write` | Re-checks the payload engine key before replay. |
| `POST /v1/engine-runs:validate` | `engine-runs:validate` | A key with `engine-runs:write` may also call validate. |

`xray:read` is not exposed by the backend yet. X-ray read and analysis routes still use the app/operator
identity model; do not assume a service API key can poll analyses or read imported portfolios until Auspicia
adds and documents that scope.

## Key design checklist

- Give a key only the scopes it needs.
- For daily engine-run keys, set `engineKeys` to the exact engine keys the integration may submit, or `*`
  only for a trusted multi-engine service.
- Rotate keys by creating a replacement, deploying it, confirming successful use, then revoking the old key.
- Do not log the plaintext key, include it in support tickets, or commit it to source control.
- Treat `401` as missing, invalid, expired, or revoked key; treat `403` as wrong scope, wrong org, or wrong
  engine key.
