using System.Buffers;
using System.Text;
using System.Text.Json;

using KetPsi.Extensions.Logging.Abstractions;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

/// <summary>
/// Expands a JSON object into a single OTLP attribute whose value is a nested
/// <c>kvlist</c> (map). Backends such as OpenObserve display it as:
/// <code>
/// user: { Aud: "api", TenantId: "1242", ContextLevel: "tenant", ... }
/// </code>
/// Used by <c>:json:indexed</c>. Null properties are skipped.
/// </summary>
internal static class OtlpJsonIndexedExpander
{
    private const uint LogAttrField = 6;       // LogRecord.attributes
    private const uint AnyValueKvList = 6;     // AnyValue.kvlist_value
    private const uint KvListValues = 1;       // KeyValueList.values

    public static int CalculateSizeFromJson(ReadOnlySpan<char> attributeName, ReadOnlySpan<byte> jsonUtf8)
    {
        if (jsonUtf8.IsEmpty) return 0;

        int inner = MeasureObjectPayload(jsonUtf8);
        if (inner < 0)
        {
            string s = Encoding.UTF8.GetString(jsonUtf8);
            return OtlpAttributeFormatter.CalculateStringSize(attributeName, s.AsSpan(), LogAttrField);
        }

        return MeasureMapAttribute(attributeName, inner);
    }

    public static void WriteFromJson(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> attributeName, ReadOnlySpan<byte> jsonUtf8)
    {
        if (jsonUtf8.IsEmpty) return;

        int inner = MeasureObjectPayload(jsonUtf8);
        if (inner < 0)
        {
            string s = Encoding.UTF8.GetString(jsonUtf8);
            OtlpAttributeFormatter.WriteString(ref writer, attributeName, s.AsSpan(), LogAttrField);
            return;
        }

        WriteMapAttribute(ref writer, attributeName, jsonUtf8, inner);
    }

    // ------------------------------------------------------------------
    // Measure
    // ------------------------------------------------------------------

    /// <summary>
    /// Size of repeated KeyValue messages for a root JSON object (KeyValueList.values payload).
    /// Returns -1 if root is not an object.
    /// </summary>
    private static int MeasureObjectPayload(ReadOnlySpan<byte> jsonUtf8)
    {
        var reader = new Utf8JsonReader(jsonUtf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return -1;

        return MeasureObjectContents(ref reader, jsonUtf8);
    }

    private static int MeasureObjectContents(ref Utf8JsonReader reader, ReadOnlySpan<byte> jsonUtf8)
    {
        int total = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return total;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string name = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                return total;

            int kv = MeasurePropertyKv(name, ref reader, jsonUtf8);
            if (kv > 0)
                total += kv;
        }
        return total;
    }

    private static int MeasurePropertyKv(string name, ref Utf8JsonReader reader, ReadOnlySpan<byte> jsonUtf8)
    {
        int anyPayload = MeasureAnyValuePayload(ref reader, jsonUtf8);
        if (anyPayload < 0)
            return 0;

        int keyLen = Encoding.UTF8.GetByteCount(name);
        int keyField = 1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen;
        int anyField = 1 + ProtobufMath.GetVarIntSize((ulong)anyPayload) + anyPayload;
        int kvPayload = keyField + anyField;

        return ProtobufMath.GetTagSize(KvListValues)
             + ProtobufMath.GetVarIntSize((ulong)kvPayload)
             + kvPayload;
    }

    private static int MeasureAnyValuePayload(ref Utf8JsonReader reader, ReadOnlySpan<byte> jsonUtf8)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                int len = Encoding.UTF8.GetByteCount(reader.GetString() ?? string.Empty);
                return 1 + ProtobufMath.GetVarIntSize((ulong)len) + len; // string_value = 1
            }
            case JsonTokenType.Number:
            {
                if (reader.TryGetInt64(out long l))
                    return 1 + ProtobufMath.GetVarIntSize((ulong)l); // int_value = 3
                return 1 + 8; // double_value = 4
            }
            case JsonTokenType.True:
            case JsonTokenType.False:
                return 2; // bool_value = 2
            case JsonTokenType.Null:
                return -1;
            case JsonTokenType.StartObject:
            {
                int nestedInner = MeasureObjectContents(ref reader, jsonUtf8);
                // AnyValue.kvlist_value envelope
                return 1 + ProtobufMath.GetVarIntSize((ulong)nestedInner) + nestedInner;
            }
            case JsonTokenType.StartArray:
            {
                string raw = SliceRaw(ref reader, jsonUtf8);
                int len = Encoding.UTF8.GetByteCount(raw);
                return 1 + ProtobufMath.GetVarIntSize((ulong)len) + len;
            }
            default:
                return -1;
        }
    }

    private static int MeasureMapAttribute(ReadOnlySpan<char> name, int innerKvlistPayload)
    {
        int keyLen = Encoding.UTF8.GetByteCount(name);
        int keyField = 1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen;

        // AnyValue payload = tag(6,2)+len+inner
        int anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)innerKvlistPayload) + innerKvlistPayload;
        int anyField = 1 + ProtobufMath.GetVarIntSize((ulong)anyPayload) + anyPayload;
        int kvPayload = keyField + anyField;

        return ProtobufMath.GetTagSize(LogAttrField)
             + ProtobufMath.GetVarIntSize((ulong)kvPayload)
             + kvPayload;
    }

    // ------------------------------------------------------------------
    // Write
    // ------------------------------------------------------------------

    private static void WriteMapAttribute(
        ref ZeroAllocProtobufWriter writer,
        ReadOnlySpan<char> attributeName,
        ReadOnlySpan<byte> jsonUtf8,
        int innerKvlistPayload)
    {
        int keyLen = Encoding.UTF8.GetByteCount(attributeName);
        int keyField = 1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen;

        int anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)innerKvlistPayload) + innerKvlistPayload;
        int anyField = 1 + ProtobufMath.GetVarIntSize((ulong)anyPayload) + anyPayload;
        int kvPayload = keyField + anyField;

        writer.WriteTag(LogAttrField, 2);
        writer.WriteVarInt((ulong)kvPayload);

        writer.WriteString(1, attributeName);

        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyPayload);

        writer.WriteTag(AnyValueKvList, 2);
        writer.WriteVarInt((ulong)innerKvlistPayload);

        WriteObjectContents(ref writer, jsonUtf8);
    }

    private static void WriteObjectContents(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<byte> jsonUtf8)
    {
        var reader = new Utf8JsonReader(jsonUtf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return;

        WriteObjectProperties(ref writer, ref reader, jsonUtf8);
    }

    private static void WriteObjectProperties(
        ref ZeroAllocProtobufWriter writer,
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> jsonUtf8)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string name = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                return;

            WritePropertyKv(ref writer, name, ref reader, jsonUtf8);
        }
    }

    private static void WritePropertyKv(
        ref ZeroAllocProtobufWriter writer,
        string name,
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> jsonUtf8)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return;

        // Measure this property's AnyValue payload (does not advance a copy — we use token type now).
        // For StartObject/StartArray we must capture positions before writing.
        long tokenStart = reader.TokenStartIndex;
        JsonTokenType token = reader.TokenType;

        int anyPayload;
        string? strVal = null;
        long intVal = 0;
        double dblVal = 0;
        bool boolVal = false;
        bool isInt = false;
        int nestedInner = 0;
        string? arrayJson = null;

        switch (token)
        {
            case JsonTokenType.String:
                strVal = reader.GetString() ?? string.Empty;
                anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)Encoding.UTF8.GetByteCount(strVal)) + Encoding.UTF8.GetByteCount(strVal);
                break;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out intVal))
                {
                    isInt = true;
                    anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)intVal);
                }
                else
                {
                    dblVal = reader.GetDouble();
                    anyPayload = 1 + 8;
                }
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                boolVal = token == JsonTokenType.True;
                anyPayload = 2;
                break;
            case JsonTokenType.StartObject:
            {
                // Measure nested without a second full reparse of parent: walk children
                nestedInner = MeasureObjectContents(ref reader, jsonUtf8);
                anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)nestedInner) + nestedInner;
                // reader is now past EndObject — we need the nested JSON slice to re-write
                break;
            }
            case JsonTokenType.StartArray:
                arrayJson = SliceRaw(ref reader, jsonUtf8);
                anyPayload = 1 + ProtobufMath.GetVarIntSize((ulong)Encoding.UTF8.GetByteCount(arrayJson)) + Encoding.UTF8.GetByteCount(arrayJson);
                break;
            default:
                return;
        }

        int keyLen = Encoding.UTF8.GetByteCount(name);
        int keyField = 1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen;
        int anyField = 1 + ProtobufMath.GetVarIntSize((ulong)anyPayload) + anyPayload;
        int kvPayload = keyField + anyField;

        writer.WriteTag(KvListValues, 2);
        writer.WriteVarInt((ulong)kvPayload);

        writer.WriteString(1, name.AsSpan());

        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyPayload);

        switch (token)
        {
            case JsonTokenType.String:
                writer.WriteString(1, strVal.AsSpan());
                break;
            case JsonTokenType.Number:
                if (isInt)
                {
                    writer.WriteTag(3, 0);
                    writer.WriteVarInt((ulong)intVal);
                }
                else
                {
                    writer.WriteTag(4, 1);
                    writer.WriteFixed64(BitConverter.DoubleToUInt64Bits(dblVal));
                }
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                writer.WriteTag(2, 0);
                writer.WriteVarInt(boolVal ? 1u : 0u);
                break;
            case JsonTokenType.StartObject:
            {
                writer.WriteTag(AnyValueKvList, 2);
                writer.WriteVarInt((ulong)nestedInner);
                // Re-parse nested object from original buffer slice
                int start = checked((int)tokenStart);
                int end = checked((int)reader.BytesConsumed);
                if (start >= 0 && end <= jsonUtf8.Length && start < end)
                {
                    WriteObjectContents(ref writer, jsonUtf8[start..end]);
                }
                break;
            }
            case JsonTokenType.StartArray:
                writer.WriteString(1, arrayJson.AsSpan());
                break;
        }
    }

    private static string SliceRaw(ref Utf8JsonReader reader, ReadOnlySpan<byte> jsonUtf8)
    {
        long start = reader.TokenStartIndex;
        reader.Skip();
        int end = checked((int)reader.BytesConsumed);
        int s = checked((int)start);
        if (s < 0 || end > jsonUtf8.Length || s >= end)
            return "[]";
        return Encoding.UTF8.GetString(jsonUtf8[s..end]);
    }
}
