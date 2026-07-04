using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auspicia.Engine;

/// <summary>
/// Reflection-free JSON value for dynamic engine params and attributes. Native AOT apps should use this
/// instead of <c>object</c> so System.Text.Json never has to discover runtime types.
/// </summary>
[JsonConverter(typeof(EngineJsonValueConverter))]
public readonly struct EngineJsonValue
{
    private readonly object? _value;

    public EngineJsonValue(object? value) => _value = Normalize(value);

    internal object? Value => _value;

    public static EngineJsonValue Null => new(null);

    public static EngineJsonValue Object(IReadOnlyDictionary<string, EngineJsonValue> value) => new(value);

    public static EngineJsonValue Array(IEnumerable<EngineJsonValue> value) => new(value);

    public static implicit operator EngineJsonValue(string? value) => new(value);
    public static implicit operator EngineJsonValue(bool value) => new(value);
    public static implicit operator EngineJsonValue(byte value) => new(value);
    public static implicit operator EngineJsonValue(short value) => new(value);
    public static implicit operator EngineJsonValue(int value) => new(value);
    public static implicit operator EngineJsonValue(long value) => new(value);
    public static implicit operator EngineJsonValue(float value) => new((double)value);
    public static implicit operator EngineJsonValue(double value) => new(value);
    public static implicit operator EngineJsonValue(decimal value) => new(value);
    public static implicit operator EngineJsonValue(double[] value) => new(value);
    public static implicit operator EngineJsonValue(List<double> value) => new(value);
    public static implicit operator EngineJsonValue(Dictionary<string, EngineJsonValue> value) => new(value);
    public static implicit operator EngineJsonValue(JsonElement value) => new(value.Clone());

    internal object? ToClrValue() => ToClrValue(_value);

    public override string ToString() => _value switch
    {
        null => "",
        JsonElement json => json.ToString(),
        _ => Convert.ToString(ToClrValue(), CultureInfo.InvariantCulture) ?? "",
    };

    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case EngineJsonValue jsonValue:
                return jsonValue.Value;
            case JsonElement element:
                return element.Clone();
            case string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                return value;
            case IReadOnlyDictionary<string, EngineJsonValue> typed:
                return new Dictionary<string, EngineJsonValue>(typed, StringComparer.Ordinal);
            case IDictionary<string, object?> objectDict:
                return objectDict.ToDictionary(
                    kv => kv.Key,
                    kv => new EngineJsonValue(kv.Value),
                    StringComparer.Ordinal);
            case IDictionary dict:
                return NormalizeDictionary(dict);
            case IEnumerable<EngineJsonValue> typedSeq:
                return typedSeq.ToArray();
            case IEnumerable seq when value is not string:
                return NormalizeArray(seq);
            default:
                throw new NotSupportedException(
                    $"EngineJsonValue cannot serialize runtime type {value.GetType().FullName}. " +
                    "Use scalars, arrays, dictionaries, JsonElement, or EngineJsonValue.");
        }
    }

    private static Dictionary<string, EngineJsonValue> NormalizeDictionary(IDictionary dict)
    {
        var output = new Dictionary<string, EngineJsonValue>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dict)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(key))
                throw new NotSupportedException("EngineJsonValue object keys must be non-empty strings.");
            output[key] = new EngineJsonValue(entry.Value);
        }
        return output;
    }

    private static EngineJsonValue[] NormalizeArray(IEnumerable seq)
    {
        var output = new List<EngineJsonValue>();
        foreach (var item in seq)
            output.Add(new EngineJsonValue(item));
        return output.ToArray();
    }

    private static object? ToClrValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case EngineJsonValue nested:
                return nested.ToClrValue();
            case JsonElement element:
                return JsonElementToClr(element);
            case IReadOnlyDictionary<string, EngineJsonValue> dict:
                return dict.ToDictionary(kv => kv.Key, kv => kv.Value.ToClrValue(), StringComparer.Ordinal);
            case IEnumerable<EngineJsonValue> seq:
                return seq.Select(item => item.ToClrValue()).ToList();
            default:
                return value;
        }
    }

    private static object? JsonElementToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(JsonElementToClr).ToList();
            case JsonValueKind.Object:
                return element.EnumerateObject().ToDictionary(
                    prop => prop.Name,
                    prop => JsonElementToClr(prop.Value),
                    StringComparer.Ordinal);
            default:
                return element.ToString();
        }
    }
}

public sealed class EngineJsonValueConverter : JsonConverter<EngineJsonValue>
{
    public override EngineJsonValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader);

    public override void Write(Utf8JsonWriter writer, EngineJsonValue value, JsonSerializerOptions options) =>
        WriteValue(writer, value.Value);

    private static EngineJsonValue ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return EngineJsonValue.Null;
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l) ? l : reader.GetDouble();
            case JsonTokenType.StartArray:
                return EngineJsonValue.Array(ReadArray(ref reader));
            case JsonTokenType.StartObject:
                return EngineJsonValue.Object(ReadObject(ref reader));
            default:
                throw new JsonException($"Unsupported JSON token {reader.TokenType}.");
        }
    }

    private static List<EngineJsonValue> ReadArray(ref Utf8JsonReader reader)
    {
        var values = new List<EngineJsonValue>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return values;
            values.Add(ReadValue(ref reader));
        }
        throw new JsonException("Unexpected end of JSON array.");
    }

    private static Dictionary<string, EngineJsonValue> ReadObject(ref Utf8JsonReader reader)
    {
        var values = new Dictionary<string, EngineJsonValue>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return values;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected JSON property name.");
            var name = reader.GetString() ?? "";
            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON object.");
            values[name] = ReadValue(ref reader);
        }
        throw new JsonException("Unexpected end of JSON object.");
    }

    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case EngineJsonValue nested:
                WriteValue(writer, nested.Value);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte b:
                writer.WriteNumberValue(b);
                break;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case ushort us:
                writer.WriteNumberValue(us);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case uint ui:
                writer.WriteNumberValue(ui);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case IReadOnlyDictionary<string, EngineJsonValue> dict:
                writer.WriteStartObject();
                foreach (var (key, item) in dict)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item.Value);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<EngineJsonValue> seq:
                writer.WriteStartArray();
                foreach (var item in seq)
                    WriteValue(writer, item.Value);
                writer.WriteEndArray();
                break;
            default:
                throw new NotSupportedException(
                    $"EngineJsonValue cannot serialize runtime type {value.GetType().FullName}.");
        }
    }
}
