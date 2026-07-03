# Dynamic parameters — the producer-driven model

Your engine computes more than weights. Momentum, quality, conviction, a risk regime, a factor embedding —
and that set **grows over time**. Auspicia is built so you can ship those parameters **without us changing
anything**: you *declare* each parameter in the run, and the platform consumes it *by definition* —
storing it, indexing it, and exposing it for filtering and ranking.

This is the contract for that mechanism.

---

## 1. Two pieces: declare, then attach

**Declare** each parameter once per run in `run.parameterDefs`:

```json
{ "key": "momentum", "type": "number", "unit": "z", "direction": "higher_better", "label": "Momentum (z)" }
```

**Attach** a value per name in `positions[].params`, keyed by the same `key`:

```json
{ "ticker": "AAPL", "weight": 4.10, "params": { "momentum": 0.73 } }
```

Every key you use in `params` **must** be declared in `parameterDefs` for that run — an undeclared value is
rejected (`422`), so a typo can't silently vanish.

## 2. The parameter definition

| Field | Required | Meaning |
|---|---|---|
| `key` | ✔ | Stable identifier, `^[A-Za-z][A-Za-z0-9_]*$`. This is the value key and the registry key. |
| `type` | ✔ | `number` · `integer` · `boolean` · `string` · `enum` · `vector` · `json` |
| `unit` | | Free text for the UI, e.g. `bps`, `%`, `z`, `score`. |
| `direction` | | `higher_better` · `lower_better` · `neutral` — how to rank/colour it. |
| `label` | | Human-friendly display name. Defaults to `key`. |
| `description` | | One-liner shown in tooltips. |
| `values` | for `enum` | The allowed string values. |
| `range` | | `[min, max]` hint for numeric sliders. |
| `dim` | for `vector` | Vector length. |

## 3. Types and how they're handled

| Type | Storage & use | Notes |
|---|---|---|
| `number` | Projected as numeric — **filter & rank** | Fixed 6-dp in the checksum. |
| `integer` | Projected as numeric — **filter & rank** | Truncated toward zero. |
| `boolean` | Projected as boolean — **filter** | |
| `string` | Projected as text — **filter (exact/contains)** | |
| `enum` | Projected as text — **filter (multi-select)** | Value outside `values` is flagged (kept). |
| `vector` | Stored verbatim; declared with `dim` | **Not** filtered/ranked yet — reserved for similarity search (dossier-level). |
| `json` | Stored verbatim | Escape hatch for structured blobs; not projected. Avoid floats (see [CHECKSUM.md](CHECKSUM.md#4-canonicaljson-only-for-type-json)). |

Whatever you send is **always** kept verbatim as the source of truth. Scalar types are *additionally*
projected into a typed table so the screener can filter and rank by them.

## 4. Type stability — the one rule

A parameter's `type` is **fixed for a given `schemaVersion`**. If you re-declare `momentum` as `integer`
after it was registered as `number` (same `schemaVersion`), the run is rejected:

```
422  parameter 'momentum' is already registered as type 'number' for schemaVersion 1.0;
     cannot redeclare it as 'integer' in the same version
```

This protects every downstream filter and chart from a silent type flip. To genuinely re-type a parameter,
introduce a new key (e.g. `momentum_v2`). Everything else about a declaration (unit, label, range,
description, direction) can change freely and simply refreshes the registry.

## 5. Coercion — flag and keep

If a value doesn't match its declared type — say `"n/a"` arrives for a `number` — the run is **still
accepted**. The bad value is quarantined (kept, but excluded from type-aware numeric filters) and reported:

```jsonc
// in the 201 / 200 response, and in :validate
"coercionWarnings": [
  { "paramKey": "momentum", "ticker": "AAPL", "declaredType": "number", "received": "n/a",
    "reason": "expected number, got non-numeric 'n/a'" }
]
```

Run `:validate` (dry-run) to see exactly what *would* be flagged before you go live. A clean run returns
`coercionWarnings: []`.

## 6. Discovery

Everything you've declared is discoverable — for you, to confirm registration, and for the platform UI, to
build filters at runtime:

```
GET /v1/engine-parameters?engineKey=vulkan-optimizer
```

```json
{
  "parameters": [
    { "engineKey": "vulkan-optimizer", "key": "conviction", "type": "integer",
      "direction": "higher_better", "range": [1, 10], "label": "Conviction",
      "schemaVersion": "1.0", "firstSeenAt": "2026-06-18T21:05:03Z" },
    { "engineKey": "vulkan-optimizer", "key": "momentum", "type": "number", "unit": "z", "...": "..." }
  ]
}
```

## 7. Design guarantees you can build on

- **Additive & backward-compatible.** A run with no `parameterDefs` behaves — and *hashes* — exactly as
  before. Adding parameters never breaks an existing weights-only integration.
- **Grows without coordination.** New parameter next quarter? Declare it and send it. No release on our side.
- **Idempotent + safe on retry.** A new parameter declaration is registered even if the run itself dedups,
  so a retry never loses a declaration. (Changing *values* for an already-ingested day needs a new
  `idempotencyKey` — see the [integration guide](INTEGRATION-GUIDE.md#idempotency--retries).)
- **No silent loss.** Undeclared value → `422`. Type conflict → `422`. Type mismatch → flagged, not dropped.

## 8. Worked example

```json
{
  "producer": { "engineKey": "vulkan-optimizer" },
  "run": {
    "runId": "vulkan-2026-06-18", "engineKey": "vulkan-optimizer",
    "kind": "optimization", "asOf": "2026-06-18",
    "parameterDefs": [
      { "key": "momentum",   "type": "number",  "unit": "z", "direction": "higher_better" },
      { "key": "conviction", "type": "integer", "range": [1, 10], "direction": "higher_better" },
      { "key": "leveraged",  "type": "boolean" },
      { "key": "regime",     "type": "enum", "values": ["risk_on", "risk_off"] },
      { "key": "factors",    "type": "vector", "dim": 3, "description": "3-factor loading" }
    ],
    "positions": [
      { "ticker": "AAPL", "weight":  4.10,
        "params": { "momentum":  0.73, "conviction": 8, "leveraged": false, "regime": "risk_on",
                    "factors": [0.10, 0.20, -0.05] } },
      { "ticker": "NVDA", "weight": -3.25,
        "params": { "momentum": -0.11, "conviction": 3, "leveraged": true, "regime": "risk_off",
                    "factors": [0.30, -0.10, 0.05] } }
    ]
  }
}
```
