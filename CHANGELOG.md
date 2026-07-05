# Changelog

All notable changes to the Auspicia engine ingestion contract and client kit.

## [Unreleased]

### Added
- Portfolio X-ray ingestion guide for `POST /xray/portfolios:bulk`: bulk JSON shape,
  allocation/performance CSV contracts, parse-report response, partial-success semantics, and the
  separate analysis trigger.
- Client-scoped API-key guide: one-key-per-client-org semantics, the endpoint scope table, show-once
  key handling, and legacy engine-token migration notes.
- Multi-organization ingestion targeting: `GET /orgs/ingestion-targets`, top-level `targetOrgId` on
  X-ray bulk import and daily import routes, and the request-level `403`/`404` authorization errors.
- `AuspiciaXrayClient` (C#): org-target discovery, typed `201`/`207` bulk-import results with per-item
  errors, request/auth/ingest exceptions, optional service headers, and transient-failure retries.

### Changed
- Docs now call out the Iris-to-Aviana web-route rebrand explicitly: API paths and provisioned base URLs
  remain stable, while new operator links should use `/#/aviana/*` instead of legacy `/#/iris/*` redirects.
- C# clients now share one internal HTTP transport. The engine client no longer discards the final 5xx
  response body (the server's `detail` reaches `EngineIngestException`), parses problem+json details
  into auth/validation errors, and accepts `defaultHeaders` (e.g. Cloudflare Access) like the X-ray
  client. Public API unchanged apart from the new optional constructor parameter.
- C# client is Native AOT-friendly: source-generated JSON metadata and `EngineJsonValue` instead of
  dynamic `object` params.
- Docs refreshed throughout to present daily engine runs and Portfolio X-ray imports as separate paths
  with API-key auth; clarified X-ray item-level error envelopes, the finite `topN=8` analysis default,
  0-based episode `idx`, and `primary`/`nested` episode kinds.

## [1.0.0] — 2026-07-03

Initial public release of the integration kit.

### Contract (schema `1.0`)
- Canonical run envelope: `producer` + `run` (signed per-name `weight`, optional `score`/`rank`/ids).
- **Producer-driven dynamic parameters** — declare parameters in `run.parameterDefs` and attach per-name
  values in `positions[].params`. Types: `number`, `integer`, `boolean`, `string`, `enum`, `vector`, `json`.
  Additive and fully backward-compatible: a run with no parameters is unchanged in behaviour and checksum.
  - Undeclared value → `422`; type conflict within a `schemaVersion` → `422`; type mismatch → flagged
    (`coercionWarnings`) and kept, never dropped.
  - Discovery via `GET /v1/engine-parameters`.
- Endpoints: `POST /v1/engine-runs`, `POST /v1/engine-runs:validate` (dry-run),
  `POST /v1/engine-runs:csv` (legacy weights-only), `GET /v1/engine-parameters`, and reads.
- Idempotency on `(engineKey, idempotencyKey)`; append-only supersede semantics (one accepted run per day).
- Integrity checksum extended to cover declared parameters — language-stable, byte-identical across
  implementations. See [docs/CHECKSUM.md](docs/CHECKSUM.md) and
  [schema/checksum-test-vectors.json](schema/checksum-test-vectors.json).
- RFC 7807 `application/problem+json` errors.

### Kit
- [JSON Schema](schema/envelope.schema.json) for the envelope.
- Frozen checksum reference vectors.
- .NET 8 C# client (`clients/csharp/`): envelope + checksum + auth + idempotency + retries, parameter
  models, `GetParametersAsync` discovery, and a vectors-parity test.
- Guides: quick start, full integration guide, dynamic-parameter model, checksum specification.
