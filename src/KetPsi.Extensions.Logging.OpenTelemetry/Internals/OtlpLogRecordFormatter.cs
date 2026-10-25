using System;
using System.Buffers.Binary;
using System.Text;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal static class OtlpLogRecordFormatter
{
    public static int CalculateLogRecordSize(
        long timeUnixNano,
        int severityNumber,
        string severityText,
        ReadOnlySpan<char> body,
        int attributesSize,
        ReadOnlySpan<byte> traceId,
        ReadOnlySpan<byte> spanId,
        string? eventName = null)
    {
        int bodyUtf8Length = Encoding.UTF8.GetByteCount(body);
        return CalculateLogRecordSizeUtf8Body(
            timeUnixNano, severityNumber, severityText, bodyUtf8Length, attributesSize, traceId, spanId, eventName);
    }

    /// <summary>
    /// Size calculation when the body is already UTF-8 encoded (zero-alloc render path).
    /// </summary>
    public static int CalculateLogRecordSizeUtf8Body(
        long timeUnixNano,
        int severityNumber,
        string severityText,
        int bodyUtf8Length,
        int attributesSize,
        ReadOnlySpan<byte> traceId,
        ReadOnlySpan<byte> spanId,
        string? eventName = null)
        => CalculateLogRecordSizeUtf8Body(timeUnixNano, severityNumber, Encoding.UTF8.GetBytes(severityText), bodyUtf8Length, attributesSize, traceId, spanId, eventName);

    /// <summary>Size calculation with a pre-encoded severity text (hot path).</summary>
    public static int CalculateLogRecordSizeUtf8Body(
        long timeUnixNano,
        int severityNumber,
        ReadOnlySpan<byte> severityTextUtf8,
        int bodyUtf8Length,
        int attributesSize,
        ReadOnlySpan<byte> traceId,
        ReadOnlySpan<byte> spanId,
        string? eventName = null)
    {
        int size = 0;

        // Field 1: time_unix_nano (Fixed64)
        size += 9;
        // Field 11: observed_time_unix_nano (Fixed64)
        size += 9;
        // Field 2: severity_number
        size += 1 + ProtobufMath.GetVarIntSize((ulong)severityNumber);
        // Field 3: severity_text
        int textBytes = severityTextUtf8.Length;
        size += 1 + ProtobufMath.GetVarIntSize((ulong)textBytes) + textBytes;

        // Field 5: body (AnyValue -> stringValue)
        int stringValueSize = 1 + ProtobufMath.GetVarIntSize((ulong)bodyUtf8Length) + bodyUtf8Length;
        size += 1 + ProtobufMath.GetVarIntSize((ulong)stringValueSize) + stringValueSize;

        // Field 6: attributes
        size += attributesSize;

        if (!traceId.IsEmpty)
            size += 1 + ProtobufMath.GetVarIntSize((ulong)traceId.Length) + traceId.Length;
        if (!spanId.IsEmpty)
            size += 1 + ProtobufMath.GetVarIntSize((ulong)spanId.Length) + spanId.Length;

        // Field 12: event_name
        if (!string.IsNullOrEmpty(eventName))
        {
            int en = Encoding.UTF8.GetByteCount(eventName);
            size += 1 + ProtobufMath.GetVarIntSize((ulong)en) + en;
        }

        return size;
    }

    public static void WriteLogRecord(
        ref ZeroAllocProtobufWriter writer,
        long timeUnixNano,
        int severityNumber,
        string severityText,
        scoped ReadOnlySpan<char> body,
        scoped ReadOnlySpan<byte> traceId,
        scoped ReadOnlySpan<byte> spanId,
        string? eventName = null)
    {
        WriteCore(ref writer, timeUnixNano, severityNumber, Encoding.UTF8.GetBytes(severityText), traceId, spanId, bodyUtf8: default, bodyChars: body, eventName: eventName);
    }

    /// <summary>
    /// Writes a LogRecord when the body is already UTF-8 encoded (zero-alloc render path).
    /// </summary>
    public static void WriteLogRecordUtf8Body(
        ref ZeroAllocProtobufWriter writer,
        long timeUnixNano,
        int severityNumber,
        string severityText,
        scoped ReadOnlySpan<byte> bodyUtf8,
        scoped ReadOnlySpan<byte> traceId,
        scoped ReadOnlySpan<byte> spanId,
        string? eventName = null)
    {
        WriteCore(ref writer, timeUnixNano, severityNumber, Encoding.UTF8.GetBytes(severityText), traceId, spanId, bodyUtf8: bodyUtf8, bodyChars: default, eventName: eventName);
    }

    /// <summary>Writes a LogRecord with a pre-encoded severity text (hot path).</summary>
    public static void WriteLogRecordUtf8Body(
        ref ZeroAllocProtobufWriter writer,
        long timeUnixNano,
        int severityNumber,
        scoped ReadOnlySpan<byte> severityTextUtf8,
        scoped ReadOnlySpan<byte> bodyUtf8,
        scoped ReadOnlySpan<byte> traceId,
        scoped ReadOnlySpan<byte> spanId,
        string? eventName = null)
    {
        WriteCore(ref writer, timeUnixNano, severityNumber, severityTextUtf8, traceId, spanId, bodyUtf8: bodyUtf8, bodyChars: default, eventName: eventName);
    }

    private static void WriteCore(
        ref ZeroAllocProtobufWriter writer,
        long timeUnixNano,
        int severityNumber,
        scoped ReadOnlySpan<byte> severityTextUtf8,
        scoped ReadOnlySpan<byte> traceId,
        scoped ReadOnlySpan<byte> spanId,
        scoped ReadOnlySpan<byte> bodyUtf8,
        scoped ReadOnlySpan<char> bodyChars,
        string? eventName = null)
    {
        // Field 1 time_unix_nano and field 11 observed_time_unix_nano (fixed64; tags 0x09 / 0x59)
        writer.WriteTag(1, 1);
        writer.WriteFixed64((ulong)timeUnixNano);
        writer.WriteTag(11, 1);
        writer.WriteFixed64((ulong)timeUnixNano);

        writer.WriteTag(2, 0);
        writer.WriteVarInt((ulong)severityNumber);

        writer.WriteBytes(3, severityTextUtf8);

        // Field 5: body (AnyValue.stringValue)
        if (!bodyUtf8.IsEmpty)
        {
            int len = bodyUtf8.Length;
            int stringValueSize = 1 + ProtobufMath.GetVarIntSize((ulong)len) + len;
            writer.WriteTag(5, 2);
            writer.WriteVarInt((ulong)stringValueSize);
            writer.WriteTag(1, 2);
            writer.WriteVarInt((ulong)len);
            writer.WriteRawBytes(bodyUtf8);
        }
        else
        {
            int bodyBytes = Encoding.UTF8.GetByteCount(bodyChars);
            int stringValueSize = 1 + ProtobufMath.GetVarIntSize((ulong)bodyBytes) + bodyBytes;
            writer.WriteTag(5, 2);
            writer.WriteVarInt((ulong)stringValueSize);
            writer.WriteString(1, bodyChars);
        }

        if (!traceId.IsEmpty)
        {
            writer.WriteTag(9, 2);
            writer.WriteVarInt((ulong)traceId.Length);
            writer.WriteRawBytes(traceId);
        }

        if (!spanId.IsEmpty)
        {
            writer.WriteTag(10, 2);
            writer.WriteVarInt((ulong)spanId.Length);
            writer.WriteRawBytes(spanId);
        }

        // Field 12: event_name (OTLP LogRecord.event_name)
        if (!string.IsNullOrEmpty(eventName))
        {
            writer.WriteString(12, eventName.AsSpan());
        }
    }
}
