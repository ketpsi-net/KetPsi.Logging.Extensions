using System.Runtime.CompilerServices;
using System.Text;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal static class OtlpAttributeFormatter
{
    // ------------------------------------------------------------------
    // Size calculation
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateStringSize(ReadOnlySpan<char> key, ReadOnlySpan<char> value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int valueLen = Encoding.UTF8.GetByteCount(value);

        // AnyValue payload = tag(1,2) + varint(valueLen) + value bytes
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueLen) + valueLen;

        // KeyValue payload = key + (tag(2,2) + varint(anyValuePayloadSize) + anyValuePayload)
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        // Outer KeyValue envelope – use real tag size (field numbers ≥ 16 need 2+ bytes)
        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateInt64Size(ReadOnlySpan<char> key, long value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int intSize = ProtobufMath.GetVarIntSize((ulong)value); // zig-zag not needed for int64 in OTLP

        // AnyValue payload = tag(3,0) + varint(value)
        int anyValuePayloadSize = 1 + intSize;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateBoolSize(ReadOnlySpan<char> key, bool value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);

        // AnyValue payload = tag(2,0) + 1 byte (0 or 1)
        int anyValuePayloadSize = 1 + 1;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateDoubleSize(ReadOnlySpan<char> key, double value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);

        // AnyValue payload = tag(4,1) + 8 bytes fixed64
        int anyValuePayloadSize = 1 + 8;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteString(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<char> key, scoped ReadOnlySpan<char> value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int valueLen = Encoding.UTF8.GetByteCount(value);

        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueLen) + valueLen;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        // KeyValue envelope (field number is normally 1 for Resource.attributes)
        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);

        // KeyValue.key = 1
        writer.WriteString(1, key, keyLen);

        // KeyValue.value = 2 → AnyValue
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);

        // AnyValue.string_value = 1
        writer.WriteString(1, value, valueLen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt64(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> key, long value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int intSize = ProtobufMath.GetVarIntSize((ulong)value);

        int anyValuePayloadSize = 1 + intSize;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);

        writer.WriteString(1, key, keyLen);

        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);

        // AnyValue.int_value = 3 (wire type 0)
        writer.WriteTag(3, 0);
        writer.WriteVarInt((ulong)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBool(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> key, bool value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int anyValuePayloadSize = 2; // tag + 1 byte

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);

        writer.WriteString(1, key, keyLen);

        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);

        // AnyValue.bool_value = 2
        writer.WriteTag(2, 0);
        writer.WriteVarInt(value ? 1u : 0u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDouble(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> key, double value, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int anyValuePayloadSize = 1 + 8;

        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);

        writer.WriteString(1, key, keyLen);

        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);

        // AnyValue.double_value = 4 (wire type 1 = fixed64)
        writer.WriteTag(4, 1);
        writer.WriteFixed64(BitConverter.DoubleToUInt64Bits(value));
    }

    // ------------------------------------------------------------------
    // Pre-encoded UTF-8 overloads: constant keys, category names and templates are encoded once
    // instead of being measured and transcoded again for every log record.
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateStringSize(ReadOnlySpan<byte> keyUtf8, ReadOnlySpan<byte> valueUtf8, uint keyValueFieldNumber = 1)
    {
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueUtf8.Length) + valueUtf8.Length;
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyUtf8.Length) + keyUtf8.Length) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteString(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> keyUtf8, scoped ReadOnlySpan<byte> valueUtf8, uint keyValueFieldNumber = 1)
    {
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueUtf8.Length) + valueUtf8.Length;
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyUtf8.Length) + keyUtf8.Length) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);
        writer.WriteBytes(1, keyUtf8);
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);
        writer.WriteBytes(1, valueUtf8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateInt64Size(ReadOnlySpan<byte> keyUtf8, long value, uint keyValueFieldNumber = 1)
    {
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)value);
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyUtf8.Length) + keyUtf8.Length) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt64(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> keyUtf8, long value, uint keyValueFieldNumber = 1)
    {
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)value);
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyUtf8.Length) + keyUtf8.Length) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);
        writer.WriteBytes(1, keyUtf8);
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);
        writer.WriteTag(3, 0);
        writer.WriteVarInt((ulong)value);
    }

    // Char key with an already UTF-8 value (formatted values, JSON): the value is never transcoded twice.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateStringSize(ReadOnlySpan<char> key, ReadOnlySpan<byte> valueUtf8, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueUtf8.Length) + valueUtf8.Length;
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        return ProtobufMath.GetTagSize(keyValueFieldNumber)
             + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize)
             + keyValuePayloadSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteString(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<char> key, scoped ReadOnlySpan<byte> valueUtf8, uint keyValueFieldNumber = 1)
    {
        int keyLen = Encoding.UTF8.GetByteCount(key);
        int anyValuePayloadSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueUtf8.Length) + valueUtf8.Length;
        int keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);

        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);
        writer.WriteString(1, key, keyLen);
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)anyValuePayloadSize);
        writer.WriteBytes(1, valueUtf8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int KeyValueEnvelope(int keyLen, int anyValuePayloadSize, uint keyValueFieldNumber, out int keyValuePayloadSize)
    {
        keyValuePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)keyLen) + keyLen) +
            (1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize);
        return ProtobufMath.GetTagSize(keyValueFieldNumber) + ProtobufMath.GetVarIntSize((ulong)keyValuePayloadSize) + keyValuePayloadSize;
    }

    public static int CalculateDoubleSize(ReadOnlySpan<byte> keyUtf8, double value, uint keyValueFieldNumber = 1)
        => KeyValueEnvelope(keyUtf8.Length, 1 + 8, keyValueFieldNumber, out _);

    public static int CalculateBoolSize(ReadOnlySpan<byte> keyUtf8, bool value, uint keyValueFieldNumber = 1)
        => KeyValueEnvelope(keyUtf8.Length, 2, keyValueFieldNumber, out _);

    public static void WriteDouble(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> keyUtf8, double value, uint keyValueFieldNumber = 1)
    {
        KeyValueEnvelope(keyUtf8.Length, 1 + 8, keyValueFieldNumber, out int keyValuePayloadSize);
        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);
        writer.WriteBytes(1, keyUtf8);
        writer.WriteTag(2, 2);
        writer.WriteVarInt(1 + 8);
        writer.WriteTag(4, 1);
        writer.WriteFixed64(BitConverter.DoubleToUInt64Bits(value));
    }

    public static void WriteBool(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> keyUtf8, bool value, uint keyValueFieldNumber = 1)
    {
        KeyValueEnvelope(keyUtf8.Length, 2, keyValueFieldNumber, out int keyValuePayloadSize);
        writer.WriteTag(keyValueFieldNumber, 2);
        writer.WriteVarInt((ulong)keyValuePayloadSize);
        writer.WriteBytes(1, keyUtf8);
        writer.WriteTag(2, 2);
        writer.WriteVarInt(2);
        writer.WriteTag(2, 0);
        writer.WriteVarInt(value ? 1u : 0u);
    }
}
