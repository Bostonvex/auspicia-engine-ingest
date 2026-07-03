# Integrity checksum — specification

The `checksum` field lets Auspicia detect in-flight corruption and wrong-run submissions. It is **optional**
but recommended. If present, the server recomputes it and rejects the run (`422`) on mismatch.

The algorithm is **language-stable by design**: fixed-precision number formatting and a canonical
percent-encoding mean Python, C#, JavaScript, Go, etc. all produce the *same bytes*. The
[C# client](../clients/csharp/) implements it; this document lets you implement it in any language.

> **Conformance:** [`schema/checksum-test-vectors.json`](../schema/checksum-test-vectors.json) is the
> source of truth. Your implementation is correct **iff** it reproduces the `canonical` string and
> `checksum` for every case. Wire those vectors into a unit test.

---

## 1. The canonical string

```
canonical = runId "|" asOf "|" engineKey [ "|" positionSeg ]... [ "|" paramSeg ]...
checksum  = "sha256:" + lowercase_hex( SHA256( UTF8( canonical ) ) )
```

- **`runId`, `asOf`, `engineKey`** are trimmed of surrounding whitespace. `asOf` is the `YYYY-MM-DD` string.
- **Position segments** come first, then **parameter segments**. A run with no parameters therefore produces
  exactly the positions-only string — checksums for weights-only runs are unchanged forever.

### Position segments

For each position: `UPPER(ticker) ":" format6(weight)`. Sort ascending (ordinal) by the segment string
(equivalently: by ticker, then by formatted weight).

`format6(x)` = the number formatted to **exactly 6 decimal places**, round-half-to-even, using `.` as the
decimal separator and a leading `-` for negatives (e.g. `4.10 → "4.100000"`, `-3.25 → "-3.250000"`).

### Parameter segments

Only parameters **declared in `run.parameterDefs`** are included (undeclared values are freeform metadata
and are ignored here). For each position, in `(UPPER(ticker), key)` order, for each declared param whose
value is present and non-null:

```
paramSeg = UPPER(ticker) "#" key "=" render(value, declaredType)
```

Segments are ordered by ticker (ordinal), then by key (ordinal). A value that is **absent or `null`** is
**omitted entirely** (so optional params never change the hash for names that don't have them).

---

## 2. `render(value, type)`

| Declared type | Rule | Example value → rendered |
|---|---|---|
| `number`  | `format6(value)` — 6 dp, round-half-to-even | `0.732 → 0.732000`, `42 → 42.000000` |
| `integer` | decimal string of `trunc(value)` (toward zero) | `8 → 8`, `8.9 → 8`, `-3 → -3` |
| `boolean` | lowercase literal | `true → true`, `false → false` |
| `string`  | `pctEncode(NFC(value))` | `"Info Tech" → Info%20Tech` |
| `enum`    | same as `string` | `"risk_on" → risk_on` |
| `vector`  | `"[" + format6(v₀) "," format6(v₁) … "]"` in **array order** (not sorted) | `[0.1,0.2,-0.05] → [0.100000,0.200000,-0.050000]` |
| `json`    | `pctEncode( canonicalJson(value) )` — see §4 | `{"b":2,"a":1} → %7B%22a%22%3A1%2C%22b%22%3A2%7D` |

**Coercion fallback.** If a value can't be rendered as its declared type (e.g. the string `"n/a"` for a
`number`), render it as `pctEncode(NFC(str(value)))` — the string form. This is applied **identically on
both sides**, so the checksum still detects corruption. (The server *separately* flags the mismatch as a
coercion warning; the checksum is not where mismatches are reported.)

---

## 3. `pctEncode` — the canonical string encoding

`pctEncode` is **RFC 3986 percent-encoding of the UTF-8 bytes**, with the unreserved set left as-is:

```
unreserved = A–Z a–z 0–9 - _ . ~        (kept literal)
everything else                          → %XX  (UPPER-case hex of each UTF-8 byte)
```

This guarantees a rendered value can never contain a separator (`|`, `#`, `=`) that would corrupt the
canonical string. Normalize strings to **Unicode NFC** *before* encoding.

Language notes — these are already exactly `pctEncode(NFC(s))`:

| Language | Call |
|---|---|
| C#         | `Uri.EscapeDataString(s.Normalize(NormalizationForm.FormC))` |
| Python     | `urllib.parse.quote(unicodedata.normalize("NFC", s), safe="")` |
| JavaScript | `encodeURIComponent(s.normalize("NFC"))` — note JS keeps `!'()*` literal; **manually %-encode those five** to match |
| Go         | `url.QueryEscape` differs (encodes space as `+`); use a helper that matches RFC 3986 |

Worked examples: `"a|b=c #1" → a%7Cb%3Dc%20%231`, `"café" → caf%C3%A9`, `"n/a" → n%2Fa`.

---

## 4. `canonicalJson` (only for `type: "json"`)

Serialize with **object keys sorted ordinally**, **no whitespace** (`,` and `:` separators), non-ASCII left
literal, standard JSON string escaping. Equivalent to Python
`json.dumps(v, sort_keys=True, separators=(",", ":"), ensure_ascii=False)`.

> ⚠️ Floating-point numbers inside a `json` blob are **not** guaranteed byte-identical across languages
> (number formatting differs by runtime). Model numeric data as `number`/`vector` params (fixed 6 dp)
> instead of burying it in `json`. Integers, strings, booleans, null, nested objects and arrays are stable.

---

## 5. Full worked example

Run `vulkan-optimizer-2026-06-18` / `2026-06-18` / `vulkan-optimizer`, two names, four declared params:

```
parameterDefs: momentum(number), quality(integer), leveraged(boolean), regime(enum)
AAPL  weight=4.10   params: momentum=0.732 quality=8 leveraged=false regime=risk_on
NVDA  weight=-3.25  params: momentum=-0.11 quality=3 leveraged=true  regime=risk_off
```

canonical:

```
vulkan-optimizer-2026-06-18|2026-06-18|vulkan-optimizer|AAPL:4.100000|NVDA:-3.250000|AAPL#leveraged=false|AAPL#momentum=0.732000|AAPL#quality=8|AAPL#regime=risk_on|NVDA#leveraged=true|NVDA#momentum=-0.110000|NVDA#quality=3|NVDA#regime=risk_off
```

checksum:

```
sha256:07716b3fb1dffbcf83ab9620976065219fd4e7cf0104cd0f1fcf8cfc1b5ab217
```

Drop the four `parameterDefs`/`params` and you get the pre-parameters canonical
`…|vulkan-optimizer|AAPL:4.100000|NVDA:-3.250000` → `sha256:a68bb2f2…` — proving parameters are additive.

See [`schema/checksum-test-vectors.json`](../schema/checksum-test-vectors.json) for these and more
(unicode, separators, sparse/null, vectors, json, coercion).
