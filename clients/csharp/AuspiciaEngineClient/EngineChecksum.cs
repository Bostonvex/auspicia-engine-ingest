using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Auspicia.Engine;

/// <summary>
/// Language-stable integrity checksum, byte-for-byte identical to the Auspicia server
/// (see docs/CHECKSUM.md and schema/checksum-test-vectors.json). The canonical string is:
/// <code>
///   runId | asOf | engineKey | &lt;TICKER:weight&gt;… | &lt;TICKER#key=value&gt;…
/// </code>
/// Positions fold to <c>TICKER:weight</c> pairs (fixed 6-dp, sorted by ticker then formatted-weight).
/// Declared per-name params fold in AFTER the positions as <c>TICKER#key=renderedValue</c> segments,
/// sorted by (ticker, key). A run with no params produces exactly the positions-only string, so weights-only
/// runs are unchanged. Only params DECLARED in <see cref="EngineRun.ParameterDefs"/> are covered.
/// </summary>
public static class EngineChecksum
{
    public static string Compute(EngineRun run)
    {
        var canonical = Canonical(run);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The exact byte string hashed by <see cref="Compute"/> — exposed for diffing against the
    /// server's <c>run_canonical_string</c> and the reference vectors when debugging a mismatch.</summary>
    public static string Canonical(EngineRun run)
    {
        var pairs = run.Positions
            .Select(p => (
                Ticker: p.Ticker.Trim().ToUpperInvariant(),
                Weight: p.Weight.ToString("F6", CultureInfo.InvariantCulture)))
            .OrderBy(p => p.Ticker, StringComparer.Ordinal)
            .ThenBy(p => p.Weight, StringComparer.Ordinal)
            .Select(p => $"{p.Ticker}:{p.Weight}");

        var segments = new List<string> { run.RunId.Trim(), run.AsOf, run.EngineKey.Trim() };
        segments.AddRange(pairs);
        segments.AddRange(ParamSegments(run));
        return string.Join("|", segments);
    }

    // Per-name declared-param segments, in deterministic (ticker, key) order. Mirrors the server's
    // _param_checksum_entries exactly.
    private static IEnumerable<string> ParamSegments(EngineRun run)
    {
        var typeByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in run.ParameterDefs ?? Array.Empty<ParameterDefinition>())
            typeByKey[d.Key] = d.Type ?? "string";
        if (typeByKey.Count == 0)
            yield break;

        foreach (var p in run.Positions
                     .OrderBy(p => p.Ticker.Trim().ToUpperInvariant(), StringComparer.Ordinal))
        {
            if (p.Params is null || p.Params.Count == 0)
                continue;
            var ticker = p.Ticker.Trim().ToUpperInvariant();
            foreach (var key in p.Params.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!typeByKey.TryGetValue(key, out var type))
                    continue;   // undeclared → not part of the integrity contract
                var rendered = RenderValue(p.Params[key], type);
                if (rendered is null)
                    continue;   // absent/null → omitted (keeps weights-only hashing stable)
                yield return $"{ticker}#{key}={rendered}";
            }
        }
    }

    // Deterministic rendering of one param value by its declared type. Returns null to OMIT (null value).
    // A value that can't be rendered as its declared type falls back to its string form — identical on the
    // server — so the checksum still detects corruption while the server separately flags the coercion.
    private static string? RenderValue(object? value, string type)
    {
        value = Unwrap(value);
        if (value is null)
            return null;

        switch (type)
        {
            case "boolean":
                return value is bool b ? (b ? "true" : "false") : EncodeString(ToInvariantString(value));

            case "integer":
                if (value is bool) return EncodeString(ToInvariantString(value));
                if (value is string si)
                    return long.TryParse(si.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl)
                        ? pl.ToString(CultureInfo.InvariantCulture)
                        : EncodeString(si);
                try
                {
                    var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(d)) return EncodeString(ToInvariantString(value));
                    return ((long)Math.Truncate(d)).ToString(CultureInfo.InvariantCulture);
                }
                catch { return EncodeString(ToInvariantString(value)); }

            case "number":
                if (value is bool) return EncodeString(ToInvariantString(value));
                if (value is string sn)
                    return double.TryParse(sn.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pf) && double.IsFinite(pf)
                        ? pf.ToString("F6", CultureInfo.InvariantCulture)
                        : EncodeString(sn);
                try
                {
                    var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(d)) return EncodeString(ToInvariantString(value));
                    return d.ToString("F6", CultureInfo.InvariantCulture);
                }
                catch { return EncodeString(ToInvariantString(value)); }

            case "vector":
                if (value is IEnumerable seq && value is not string)
                {
                    var parts = new List<string>();
                    try
                    {
                        foreach (var x in seq)
                            parts.Add(Convert.ToDouble(x, CultureInfo.InvariantCulture).ToString("F6", CultureInfo.InvariantCulture));
                        return "[" + string.Join(",", parts) + "]";
                    }
                    catch { return EncodeString(ToInvariantString(value)); }
                }
                return EncodeString(ToInvariantString(value));

            case "json":
                return EncodeString(CanonicalJson.Serialize(value));

            default: // string, enum, unknown
                return EncodeString(value as string ?? ToInvariantString(value));
        }
    }

    // Unwrap a System.Text.Json JsonElement (as produced when an envelope is DESERIALIZED, rather than
    // constructed with native CLR types) into bool / long / double / string / List / Dictionary, so the
    // renderer below is identical whether params were hand-built or round-tripped through JSON.
    private static object? Unwrap(object? value)
    {
        if (value is EngineJsonValue jsonValue)
            return Unwrap(jsonValue.ToClrValue());
        if (value is not JsonElement je)
            return value;
        switch (je.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return je.GetString();
            case JsonValueKind.Number:
                return je.TryGetInt64(out var l) ? l : je.GetDouble();
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in je.EnumerateArray()) list.Add(Unwrap(item));
                return list;
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in je.EnumerateObject()) dict[prop.Name] = Unwrap(prop.Value);
                return dict;
            default:
                return je.ToString();
        }
    }

    // NFC-normalize then percent-encode (RFC 3986 unreserved set A-Za-z0-9-._~ stay; everything else →
    // %XX upper-hex of the UTF-8 bytes). Uri.EscapeDataString is exactly Python's quote(s, safe="").
    private static string EncodeString(string s) => Uri.EscapeDataString(s.Normalize(NormalizationForm.FormC));

    private static string ToInvariantString(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
