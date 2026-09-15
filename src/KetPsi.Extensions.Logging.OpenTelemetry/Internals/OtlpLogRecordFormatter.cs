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
        ReadOnlySpan<byte> spanId)
    {
        int size = 0;

        // Field 1: time_unix_nano (Fixed64 -> 1 byte tag + 8 bytes payload)
        size += 9;

        // Field 11: observed_time_unix_nano (Fixed64 -> 1 byte tag + 8 bytes payload)
        size += 9;

        // Field 2: severity_number (VarInt)
        size += 1 + ProtobufMath.GetVarIntSize((ulong)severityNumber);

        // Field 3: severity_text (Length-delimited String)
        int textBytes = Encoding.UTF8.GetByteCount(severityText);
        size += 1 + ProtobufMath.GetVarIntSize((ulong)textBytes) + textBytes;

        // Field 5: body (AnyValue -> stringValue)
        int bodyBytes = Encoding.UTF8.GetByteCount(body);
        int stringValueSize = 1 + ProtobufMath.GetVarIntSize((ulong)bodyBytes) + bodyBytes;
        size += 1 + ProtobufMath.GetVarIntSize((ulong)stringValueSize) + stringValueSize;

        // Field 6: attributes (repeated KeyValue)
        size += attributesSize;

        // Field 9: trace_id
        if (!traceId.IsEmpty)
        {
            size += 1 + ProtobufMath.GetVarIntSize((ulong)traceId.Length) + traceId.Length;
        }

        // Field 10: span_id
        if (!spanId.IsEmpty)
        {
            size += 1 + ProtobufMath.GetVarIntSize((ulong)spanId.Length) + spanId.Length;
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
        scoped ReadOnlySpan<byte> spanId)
    {
        // Field 1: time_unix_nano (Tag 1, WireType 1 [Fixed64] = 0x09)
        // We use stackalloc to create a 9-byte buffer on the stack (zero heap allocation)
        Span<byte> timeBytes = stackalloc byte[9];
        timeBytes[0] = 0x09;
        BinaryPrimitives.WriteInt64LittleEndian(timeBytes.Slice(1), timeUnixNano);
        writer.WriteRawBytes(timeBytes);

        // Field 11: observed_time_unix_nano (Tag 11, WireType 1 [Fixed64] = 0x59)
        Span<byte> obsTimeBytes = stackalloc byte[9];
        obsTimeBytes[0] = 0x59;
        BinaryPrimitives.WriteInt64LittleEndian(obsTimeBytes.Slice(1), timeUnixNano);
        writer.WriteRawBytes(obsTimeBytes);

        // Field 2: severity_number (Tag 2, WireType 0 [VarInt])
        writer.WriteTag(2, 0);
        writer.WriteVarInt((ulong)severityNumber);

        // Field 3: severity_text (String)
        // We can use your exact WriteString method which handles Tag + Length + Body
        writer.WriteString(3, severityText.AsSpan());

        // Field 5: body (AnyValue wrapper)
        int bodyBytes = Encoding.UTF8.GetByteCount(body);
        int stringValueSize = 1 + ProtobufMath.GetVarIntSize((ulong)bodyBytes) + bodyBytes;

        writer.WriteTag(5, 2);
        writer.WriteVarInt((ulong)stringValueSize);

        // Field 1 inside AnyValue is stringValue. Your WriteString perfectly handles this!
        writer.WriteString(1, body);

        // Field 9: trace_id
        if (!traceId.IsEmpty)
        {
            writer.WriteTag(9, 2);
            writer.WriteVarInt((ulong)traceId.Length);
            writer.WriteRawBytes(traceId);
        }

        // Field 10: span_id
        if (!spanId.IsEmpty)
        {
            writer.WriteTag(10, 2);
            writer.WriteVarInt((ulong)spanId.Length);
            writer.WriteRawBytes(spanId);
        }
    }
}