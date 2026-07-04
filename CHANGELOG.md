# Changelog

All notable changes to the Auspicia engine ingestion contract and client kit.

## [Unreleased]

### Docs
- Added client-scoped API-key guidance, including one-key-per-client-org semantics, endpoint scope
  requirements, show-once key handling, LampShade examples, and legacy engine-token migration notes.
- Updated the README, quickstart, integration guide, Portfolio X-ray guide, and C# docs to use API keys for
  daily engine runs, X-ray bulk import, target discovery, and daily import routes.
- Documented live multi-organization ingestion targeting: `GET /orgs/ingestion-targets`, top-level
  `targetOrgId` on X-ray bulk import and daily import routes, and the request-level `403`/`404`
  authorization errors.
- Added the Portfolio X-ray ingestion guide for `POST /xray/portfolios:bulk`, including the bulk JSON
  shape, allocation/performance CSV contracts, parse-report response, partial-success semantics, and the
  separate analysis trigger.
- Linked the X-ray ingestion contract from the README, quick start, and integration guide.
- Clarified X-ray item-level error envelopes, finite `topN=8` analysis default, 0-based episode indexes,
  and `primary`/`nested` episode kinds.
- Refreshed the README so the repo presents both daily engine-run ingestion and Portfolio X-ray historical
  imports as first-class integration paths.
- Added `AuspiciaXrayClient` for C# callers, with typed `201`/`207` bulk import results, request/auth/ingest
  exceptions, service-header support, and retry handling for transient failures.
- Added C# X-ray helpers for org-target discovery and top-level `targetOrgId` bulk imports.

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
