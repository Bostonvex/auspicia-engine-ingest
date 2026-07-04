using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Auspicia.Engine;

/// <summary>
/// Deterministic JSON serializer used ONLY to fold a <c>json</c>-typed parameter into the integrity
/// checksum. Matches Python's <c>json.dumps(value, sort_keys=True, separators=(",", ":"),
/// ensure_ascii=False)</c>: object keys sorted by ordinal, compact separators, non-ASCII left literal.
///
/// Note: <c>json</c> params are an escape hatch. Integers, strings, booleans, null, nested objects and
/// arrays are byte-stable across languages; floating-point numbers inside a json blob are NOT guaranteed
/// identical (formatting differs by runtime) — model numeric data as <c>number</c>/<c>vector</c> params
/// (fixed 6-dp) instead. See docs/CHECKSUM.md.
/// </summary>
internal static class CanonicalJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case EngineJsonValue jsonValue:
                Write(sb, jsonValue.ToClrValue());
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case IDictionary dict:
                WriteObject(sb, dict);
                break;
            case IEnumerable seq:
                WriteArray(sb, seq);
                break;
            default:
                WriteNumber(sb, value);
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, IDictionary dict)
    {
        var keys = dict.Keys.Cast<object>()
            .Select(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? "")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        sb.Append('{');
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteString(sb, keys[i]);
            sb.Append(':');
            Write(sb, dict[keys[i]]);
        }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, IEnumerable seq)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in seq)
        {
            if (!first) sb.Append(',');
            first = false;
            Write(sb, item);
        }
        sb.Append(']');
    }

    private static void WriteNumber(StringBuilder sb, object value)
    {
        // Integers render without a decimal point (matching Python json). Non-integral doubles use the
        // round-trip format; avoid floats in json params if cross-language parity matters (see class note).
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                sb.Append(d == Math.Truncate(d) && !double.IsInfinity(d)
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    // JSON string escaping identical to Python's json encoder (ensure_ascii=False): escape " and \, the
    // seven short control escapes, and any remaining C0 control char as \u00XX. Everything else literal.
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
