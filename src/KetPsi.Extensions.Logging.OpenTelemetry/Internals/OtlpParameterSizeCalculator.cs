using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

using KetPsi.Extensions.Logging.Abstractions;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

/// <summary>
/// Size pass over a log state's parameters. When <see cref="Arena"/> is set, every non-primitive value is
/// formatted to UTF-8 exactly once into it, and <see cref="OtlpParameterEncoder"/> replays those bytes in the
/// same order (generated <c>WriteParameters</c> always visits parameters in declaration order).
/// </summary>
internal struct OtlpParameterSizeCalculator : ILogParameterWriter
{
    // LogRecord.attributes is field 6 in the OTLP protobuf schema.
    private const uint LogAttributeFieldNumber = 6;

    public int TotalSize;

    /// <summary>Optional per-log buffer holding formatted values for the encoder to reuse.</summary>
    public PooledByteBufferWriter? Arena;

    /// <summary>Optional rendered message whose recorded value ranges are reused instead of formatting values again.</summary>
    public BodyValues Body;

    private int _index;

    public void Write<T>(in LogParameter<T> parameter)
    {
        // :json:indexed — expand object properties to prefix.prop attributes
        int index = _index++;

        if (parameter.Format == "json:indexed")
        {
            TotalSize += ExpandIndexedSize(parameter.Name.AsSpan(), parameter.Value);
            return;
        }

        Span<byte> nameBuffer = parameter.NameUtf8 is null ? stackalloc byte[OtlpNames.StackLimit] : default;
        ReadOnlySpan<byte> name = OtlpNames.GetUtf8(in parameter, nameBuffer);

        if (OtlpPrimitive.IsPrimitive<T>())
        {
            TotalSize += OtlpPrimitive.CalculateSize(name, in parameter.Value, LogAttributeFieldNumber);
        }
        else if (Body.TryGet(in parameter, index, out ReadOnlySpan<byte> rendered))
        {
            // Formatted once already, while rendering the message body.
            TotalSize += OtlpAttributeFormatter.CalculateStringSize(name, rendered, LogAttributeFieldNumber);
        }
        else if (Arena != null)
        {
            ReadOnlySpan<byte> value = OtlpValueArena.Append(Arena, in parameter.Value);
            TotalSize += OtlpAttributeFormatter.CalculateStringSize(name, value, LogAttributeFieldNumber);
        }
        else
        {
            TotalSize += OtlpStringValue.CalculateSize(name, in parameter.Value, LogAttributeFieldNumber);
        }
    }

    private static int ExpandIndexedSize<T>(ReadOnlySpan<char> prefix, T value)
    {
        // Type check instead of `value is IDeferredUtf8Formatter`, which would box the JsonLogValue struct.
        if (value is not null && typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(T)))
        {
            var buffer = PooledByteBufferWriter.Rent(ref OtlpJsonScratch.Writer);
            try
            {
                LogValueFormatter.FormatTo(in value, buffer, null);
                return OtlpJsonIndexedExpander.CalculateSizeFromJson(prefix, buffer.WrittenSpan);
            }
            finally
            {
                PooledByteBufferWriter.Return(ref OtlpJsonScratch.Writer, buffer);
            }
        }

        string s = value?.ToString() ?? string.Empty;
        return OtlpAttributeFormatter.CalculateStringSize(prefix, s.AsSpan(), LogAttributeFieldNumber);
    }
}

/// <summary>Write pass matching <see cref="OtlpParameterSizeCalculator"/> byte for byte.</summary>
internal unsafe struct OtlpParameterEncoder : ILogParameterWriter
{
    private const uint LogAttributeFieldNumber = 6;

    private readonly byte* _ptr;
    private readonly int _length;
    private readonly PooledByteBufferWriter? _arena;
    private readonly BodyValues _body;
    private int _offset;
    private int _arenaOffset;
    private int _index;

    public OtlpParameterEncoder(byte* ptr, int offset, int length)
        : this(ptr, offset, length, null)
    {
    }

    /// <param name="arena">The arena filled by the size pass, or <see langword="null"/> to format values directly.</param>
    public OtlpParameterEncoder(byte* ptr, int offset, int length, PooledByteBufferWriter? arena)
        : this(ptr, offset, length, arena, default)
    {
    }

    /// <param name="body">The rendered message whose value ranges the size pass reused (same rule applies here).</param>
    public OtlpParameterEncoder(byte* ptr, int offset, int length, PooledByteBufferWriter? arena, BodyValues body)
    {
        _ptr = ptr;
        _offset = offset;
        _length = length;
        _arena = arena;
        _body = body;
        _arenaOffset = 0;
        _index = 0;
    }

    public void Write<T>(in LogParameter<T> parameter)
    {
        var writer = new ZeroAllocProtobufWriter(new Span<byte>(_ptr + _offset, _length - _offset));
        int index = _index++;

        if (parameter.Format == "json:indexed")
        {
            ExpandIndexedWrite(ref writer, parameter.Name.AsSpan(), parameter.Value);
            _offset += writer.BytesWritten;
            return;
        }

        Span<byte> nameBuffer = parameter.NameUtf8 is null ? stackalloc byte[OtlpNames.StackLimit] : default;
        ReadOnlySpan<byte> name = OtlpNames.GetUtf8(in parameter, nameBuffer);

        if (OtlpPrimitive.IsPrimitive<T>())
        {
            OtlpPrimitive.Write(ref writer, name, in parameter.Value, LogAttributeFieldNumber);
        }
        else if (_body.TryGet(in parameter, index, out ReadOnlySpan<byte> rendered))
        {
            OtlpAttributeFormatter.WriteString(ref writer, name, rendered, LogAttributeFieldNumber);
        }
        else if (_arena != null)
        {
            ReadOnlySpan<byte> arena = _arena.WrittenSpan;
            int length = BinaryPrimitives.ReadInt32LittleEndian(arena.Slice(_arenaOffset));
            OtlpAttributeFormatter.WriteString(ref writer, name, arena.Slice(_arenaOffset + sizeof(int), length), LogAttributeFieldNumber);
            _arenaOffset += sizeof(int) + length;
        }
        else
        {
            OtlpStringValue.Write(ref writer, name, in parameter.Value, LogAttributeFieldNumber);
        }

        _offset += writer.BytesWritten;
    }

    private static void ExpandIndexedWrite<T>(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> prefix, T value)
    {
        // Type check instead of `value is IDeferredUtf8Formatter`, which would box the JsonLogValue struct.
        if (value is not null && typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(T)))
        {
            var buffer = PooledByteBufferWriter.Rent(ref OtlpJsonScratch.Writer);
            try
            {
                LogValueFormatter.FormatTo(in value, buffer, null);
                OtlpJsonIndexedExpander.WriteFromJson(ref writer, prefix, buffer.WrittenSpan);
            }
            finally
            {
                PooledByteBufferWriter.Return(ref OtlpJsonScratch.Writer, buffer);
            }
            return;
        }

        string s = value?.ToString() ?? string.Empty;
        OtlpAttributeFormatter.WriteString(ref writer, prefix, s.AsSpan(), LogAttributeFieldNumber);
    }
}

/// <summary>
/// The rendered message body plus the byte range of each parameter value inside it
/// (from <see cref="IStructuredLogState.FormatTo(IPositionedBufferWriter, int[])"/>).
/// Both OTLP passes use <see cref="TryGet{T}"/>, so they always agree on which values come from the body.
/// </summary>
internal readonly struct BodyValues(PooledByteBufferWriter? body, int[]? ranges)
{
    private readonly PooledByteBufferWriter? _body = body;
    private readonly int[]? _ranges = ranges;

    /// <summary>
    /// The value's rendered bytes, when they are exactly what the attribute would contain: the attribute is
    /// formatted with no format string, so values rendered with a format specifier (e.g. <c>:N2</c>) or padded
    /// by alignment are formatted again. JSON and masked values ignore the format string, so they always qualify.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<T>(in LogParameter<T> parameter, int index, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (_body is null || _ranges is null || 2 * index + 1 >= _ranges.Length) return false;
        if (parameter.Format != null && !typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(T))) return false;

        int start = _ranges[2 * index];
        if (start < 0) return false;

        value = _body.WrittenSpan.Slice(start, _ranges[2 * index + 1]);
        return true;
    }
}

/// <summary>Parameter values that map to native OTLP AnyValue kinds (int, double, bool) rather than strings.</summary>
internal static class OtlpPrimitive
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrimitive<T>()
        => typeof(T) == typeof(long) || typeof(T) == typeof(int) || typeof(T) == typeof(short) || typeof(T) == typeof(byte)
        || typeof(T) == typeof(double) || typeof(T) == typeof(float) || typeof(T) == typeof(bool);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateSize<T>(ReadOnlySpan<byte> name, in T value, uint fieldNumber)
    {
        ref T v = ref Unsafe.AsRef(in value);
        if (typeof(T) == typeof(long)) return OtlpAttributeFormatter.CalculateInt64Size(name, Unsafe.As<T, long>(ref v), fieldNumber);
        if (typeof(T) == typeof(int)) return OtlpAttributeFormatter.CalculateInt64Size(name, Unsafe.As<T, int>(ref v), fieldNumber);
        if (typeof(T) == typeof(short)) return OtlpAttributeFormatter.CalculateInt64Size(name, Unsafe.As<T, short>(ref v), fieldNumber);
        if (typeof(T) == typeof(byte)) return OtlpAttributeFormatter.CalculateInt64Size(name, Unsafe.As<T, byte>(ref v), fieldNumber);
        if (typeof(T) == typeof(double)) return OtlpAttributeFormatter.CalculateDoubleSize(name, Unsafe.As<T, double>(ref v), fieldNumber);
        if (typeof(T) == typeof(float)) return OtlpAttributeFormatter.CalculateDoubleSize(name, Unsafe.As<T, float>(ref v), fieldNumber);
        return OtlpAttributeFormatter.CalculateBoolSize(name, Unsafe.As<T, bool>(ref v), fieldNumber);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> name, in T value, uint fieldNumber)
    {
        ref T v = ref Unsafe.AsRef(in value);
        if (typeof(T) == typeof(long)) OtlpAttributeFormatter.WriteInt64(ref writer, name, Unsafe.As<T, long>(ref v), fieldNumber);
        else if (typeof(T) == typeof(int)) OtlpAttributeFormatter.WriteInt64(ref writer, name, Unsafe.As<T, int>(ref v), fieldNumber);
        else if (typeof(T) == typeof(short)) OtlpAttributeFormatter.WriteInt64(ref writer, name, Unsafe.As<T, short>(ref v), fieldNumber);
        else if (typeof(T) == typeof(byte)) OtlpAttributeFormatter.WriteInt64(ref writer, name, Unsafe.As<T, byte>(ref v), fieldNumber);
        else if (typeof(T) == typeof(double)) OtlpAttributeFormatter.WriteDouble(ref writer, name, Unsafe.As<T, double>(ref v), fieldNumber);
        else if (typeof(T) == typeof(float)) OtlpAttributeFormatter.WriteDouble(ref writer, name, Unsafe.As<T, float>(ref v), fieldNumber);
        else OtlpAttributeFormatter.WriteBool(ref writer, name, Unsafe.As<T, bool>(ref v), fieldNumber);
    }
}

/// <summary>Parameter names as UTF-8: the generator's pre-encoded bytes, or encoded on the spot for hand-built parameters.</summary>
internal static class OtlpNames
{
    public const int StackLimit = 128;

    public static ReadOnlySpan<byte> GetUtf8<T>(in LogParameter<T> parameter, Span<byte> buffer)
    {
        if (parameter.NameUtf8 is { } utf8) return utf8;

        string? name = parameter.Name;
        if (string.IsNullOrEmpty(name)) return default;
        if (Encoding.UTF8.TryGetBytes(name, buffer, out int written)) return buffer[..written];
        return Encoding.UTF8.GetBytes(name); // names longer than the stack buffer; never produced by the generator
    }
}

/// <summary>Appends length-prefixed UTF-8 renderings of parameter values to the per-log arena.</summary>
internal static class OtlpValueArena
{
    private const int MaxValueBytes = 1 << 20;

    public static ReadOnlySpan<byte> Append<T>(PooledByteBufferWriter arena, in T value)
    {
        int lengthOffset = arena.WrittenCount;
        arena.GetSpan(sizeof(int));
        arena.Advance(sizeof(int));
        int start = arena.WrittenCount;

        if (typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(T)))
        {
            // JSON / masked values render to UTF-8 natively (JsonLogValue reuses its per-log serialization).
            LogValueFormatter.FormatTo(in value, arena, null);
        }
        else
        {
            int sizeHint = typeof(T) == typeof(string)
                ? Encoding.UTF8.GetMaxByteCount(Unsafe.As<T, string?>(ref Unsafe.AsRef(in value))?.Length ?? 0)
                : 64;
            while (true)
            {
                Span<byte> destination = arena.GetSpan(sizeHint);
                if (LogValueFormatter.TryFormatUtf8(in value, destination, out int written, null))
                {
                    arena.Advance(written);
                    break;
                }
                if (destination.Length >= MaxValueBytes) break; // give up: record an empty value
                sizeHint = destination.Length * 2;
            }
        }

        int length = arena.WrittenCount - start;
        BinaryPrimitives.WriteInt32LittleEndian(arena.GetWrittenSpanMutable().Slice(lengthOffset), length);
        return arena.WrittenSpan.Slice(start, length);
    }
}

/// <summary>Per-thread scratch buffer for expanding <c>:json:indexed</c> values and staging UTF-8 values without an arena.</summary>
internal static class OtlpJsonScratch
{
    [ThreadStatic]
    public static PooledByteBufferWriter? Writer;
}

/// <summary>
/// Renders a non-primitive parameter value (string, Guid, decimal, JSON, masked, custom types) as a UTF-8 string
/// attribute through the generator-registered <see cref="LogValueFormatter{T}"/>: no boxing, no reflection, and
/// no UTF-16 round trip. Used when no arena is available (scopes, hand-built parameters).
/// </summary>
internal static class OtlpStringValue
{
    public static int CalculateSize<T>(scoped ReadOnlySpan<char> name, in T value, uint fieldNumber)
    {
        Span<byte> nameUtf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
        return CalculateSize(nameUtf8[..Encoding.UTF8.GetBytes(name, nameUtf8)], in value, fieldNumber);
    }

    public static void Write<T>(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<char> name, in T value, uint fieldNumber)
    {
        Span<byte> nameUtf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
        Write(ref writer, nameUtf8[..Encoding.UTF8.GetBytes(name, nameUtf8)], in value, fieldNumber);
    }

    public static int CalculateSize<T>(scoped ReadOnlySpan<byte> name, in T value, uint fieldNumber)
    {
        var scratch = PooledByteBufferWriter.Rent(ref OtlpJsonScratch.Writer);
        try
        {
            ReadOnlySpan<byte> utf8 = OtlpValueArena.Append(scratch, in value);
            return OtlpAttributeFormatter.CalculateStringSize(name, utf8, fieldNumber);
        }
        finally
        {
            PooledByteBufferWriter.Return(ref OtlpJsonScratch.Writer, scratch);
        }
    }

    public static void Write<T>(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<byte> name, in T value, uint fieldNumber)
    {
        var scratch = PooledByteBufferWriter.Rent(ref OtlpJsonScratch.Writer);
        try
        {
            ReadOnlySpan<byte> utf8 = OtlpValueArena.Append(scratch, in value);
            OtlpAttributeFormatter.WriteString(ref writer, name, utf8, fieldNumber);
        }
        finally
        {
            PooledByteBufferWriter.Return(ref OtlpJsonScratch.Writer, scratch);
        }
    }
}
