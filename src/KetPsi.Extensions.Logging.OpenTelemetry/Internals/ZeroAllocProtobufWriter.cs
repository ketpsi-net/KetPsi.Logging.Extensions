using System.Runtime.CompilerServices;
using System.Text;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

/// <summary>
/// A zero-allocation, bare-metal Protocol Buffers writer.
/// Must be used as a ref struct to ensure it never crosses heap boundaries.
/// </summary>
internal ref struct ZeroAllocProtobufWriter
{
    private readonly Span<byte> _buffer;
    public int BytesWritten { get; private set; }

    public ZeroAllocProtobufWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        BytesWritten = 0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawBytes(scoped ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer.Slice(BytesWritten));
        BytesWritten += bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag(uint fieldNumber, uint wireType)
    {
        uint tag = (fieldNumber << 3) | wireType;
        WriteVarInt(tag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarInt(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer[BytesWritten++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[BytesWritten++] = (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFixed64(ulong value)
    {
        // OTLP uses Little-Endian for Fixed64 (like time_unix_nano)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(BytesWritten), value);
        BytesWritten += 8;
    }

    public void WriteString(uint fieldNumber, scoped ReadOnlySpan<char> value)
    {
        WriteTag(fieldNumber, 2); // WireType 2 = Length-Delimited

        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarInt((ulong)byteCount);

        Encoding.UTF8.GetBytes(value, _buffer.Slice(BytesWritten));
        BytesWritten += byteCount;
    }

    public void WriteBytes(uint fieldNumber, scoped ReadOnlySpan<byte> value)
    {
        WriteTag(fieldNumber, 2); // WireType 2 = Length-Delimited
        WriteVarInt((ulong)value.Length);

        value.CopyTo(_buffer.Slice(BytesWritten));
        BytesWritten += value.Length;
    }
}