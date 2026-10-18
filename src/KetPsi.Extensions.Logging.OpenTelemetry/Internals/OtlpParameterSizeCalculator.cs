using System.Buffers;
using System.Runtime.CompilerServices;

using KetPsi.Extensions.Logging.Abstractions;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal struct OtlpParameterSizeCalculator : ILogParameterWriter
{
    // LogRecord.attributes is field 6 in the OTLP protobuf schema.
    private const uint LogAttributeFieldNumber = 6;

    public int TotalSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in LogParameter<T> parameter)
    {
        ReadOnlySpan<char> nameSpan = parameter.Name.AsSpan();

        // :json:indexed — expand object properties to prefix.prop attributes
        if (parameter.Format == "json:indexed")
        {
            TotalSize += ExpandIndexedSize(nameSpan, parameter.Value);
            return;
        }

        Type type = typeof(T);

        // 1. Primitive Unboxed Types FIRST
        if (type == typeof(long))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, long>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(int))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, int>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(short))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, short>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(byte))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, byte>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(double))
        {
            TotalSize += OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, double>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(float))
        {
            TotalSize += OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, float>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(bool))
        {
            TotalSize += OtlpAttributeFormatter.CalculateBoolSize(nameSpan, Unsafe.As<T, bool>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref Unsafe.AsRef(in parameter.Value));
            TotalSize += OtlpAttributeFormatter.CalculateStringSize(nameSpan, s.AsSpan(), LogAttributeFieldNumber);
        }
        else
        {
            var helper = FormatterDispatcher<T>.Helper;
            if (helper != null)
            {
                TotalSize += helper.CalculateSize(nameSpan, ref Unsafe.AsRef(in parameter.Value), LogAttributeFieldNumber);
            }
            else
            {
                TotalSize += OtlpAttributeFormatter.CalculateStringSize(nameSpan, parameter.Value is null ? [] : parameter.Value.ToString().AsSpan(), LogAttributeFieldNumber);
            }
        }
    }

    private static int ExpandIndexedSize<T>(ReadOnlySpan<char> prefix, T value)
    {
        if (value is IDeferredUtf8Formatter formatter)
        {
            var buffer = new PooledByteBufferWriter(256);
            try
            {
                formatter.FormatTo(buffer);
                return OtlpJsonIndexedExpander.CalculateSizeFromJson(prefix, buffer.WrittenSpan);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        string s = value?.ToString() ?? string.Empty;
        return OtlpAttributeFormatter.CalculateStringSize(prefix, s.AsSpan(), LogAttributeFieldNumber);
    }
}

internal abstract class SpanFormattableHelper<T>
{
    public abstract int CalculateSize(scoped ReadOnlySpan<char> nameSpan, ref T value, uint fieldNumber = 6);
    public abstract void Write(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<char> nameSpan, ref T value, uint fieldNumber = 6);
}

internal sealed class SpanFormattableWrapperHelper<T> : SpanFormattableHelper<T> where T : ISpanFormattable
{
    public override int CalculateSize(scoped ReadOnlySpan<char> nameSpan, ref T value, uint fieldNumber = 6)
    {
        Span<char> stackBuffer = stackalloc char[1024];
        if (value.TryFormat(stackBuffer, out int written, default, null))
        {
            return OtlpAttributeFormatter.CalculateStringSize(nameSpan, stackBuffer.Slice(0, written), fieldNumber);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            if (value.TryFormat(rented, out written, default, null))
            {
                return OtlpAttributeFormatter.CalculateStringSize(nameSpan, rented.AsSpan(0, written), fieldNumber);
            }
            return OtlpAttributeFormatter.CalculateStringSize(nameSpan, ReadOnlySpan<char>.Empty, fieldNumber);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    public override void Write(ref ZeroAllocProtobufWriter writer, scoped ReadOnlySpan<char> nameSpan, ref T value, uint fieldNumber = 6)
    {
        Span<char> stackBuffer = stackalloc char[1024];
        if (value.TryFormat(stackBuffer, out int written, default, null))
        {
            OtlpAttributeFormatter.WriteString(ref writer, nameSpan, stackBuffer.Slice(0, written), fieldNumber);
            return;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            if (value.TryFormat(rented, out written, default, null))
            {
                OtlpAttributeFormatter.WriteString(ref writer, nameSpan, rented.AsSpan(0, written), fieldNumber);
            }
            else
            {
                OtlpAttributeFormatter.WriteString(ref writer, nameSpan, ReadOnlySpan<char>.Empty, fieldNumber);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }
}

internal static class FormatterDispatcher<T>
{
    public static readonly SpanFormattableHelper<T>? Helper = CreateHelper();

    private static SpanFormattableHelper<T>? CreateHelper()
    {
        if (typeof(ISpanFormattable).IsAssignableFrom(typeof(T)))
        {
            return (SpanFormattableHelper<T>?)Activator.CreateInstance(typeof(SpanFormattableWrapperHelper<>).MakeGenericType(typeof(T)));
        }
        return null;
    }
}

internal unsafe struct OtlpParameterEncoder(byte* ptr, int offset, int length) : ILogParameterWriter
{
    private const uint LogAttributeFieldNumber = 6;

    private readonly byte* _ptr = ptr;
    private readonly int _length = length;
    private int _offset = offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in LogParameter<T> parameter)
    {
        Span<byte> span = new Span<byte>(_ptr + _offset, _length - _offset);
        var writer = new ZeroAllocProtobufWriter(span);
        ReadOnlySpan<char> nameSpan = parameter.Name.AsSpan();

        if (parameter.Format == "json:indexed")
        {
            ExpandIndexedWrite(ref writer, nameSpan, parameter.Value);
            _offset += writer.BytesWritten;
            return;
        }

        Type type = typeof(T);

        if (type == typeof(long))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, long>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(int))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, int>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(short))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, short>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(byte))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, byte>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(double))
        {
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, double>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(float))
        {
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, float>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(bool))
        {
            OtlpAttributeFormatter.WriteBool(ref writer, nameSpan, Unsafe.As<T, bool>(ref Unsafe.AsRef(in parameter.Value)), LogAttributeFieldNumber);
        }
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref Unsafe.AsRef(in parameter.Value));
            OtlpAttributeFormatter.WriteString(ref writer, nameSpan, s.AsSpan(), LogAttributeFieldNumber);
        }
        else
        {
            var helper = FormatterDispatcher<T>.Helper;
            if (helper != null)
            {
                helper.Write(ref writer, nameSpan, ref Unsafe.AsRef(in parameter.Value), LogAttributeFieldNumber);
            }
            else
            {
                OtlpAttributeFormatter.WriteString(ref writer, nameSpan, parameter.Value is null ? [] : parameter.Value.ToString().AsSpan(), LogAttributeFieldNumber);
            }
        }

        _offset += writer.BytesWritten;
    }

    private static void ExpandIndexedWrite<T>(ref ZeroAllocProtobufWriter writer, ReadOnlySpan<char> prefix, T value)
    {
        if (value is IDeferredUtf8Formatter formatter)
        {
            var buffer = new PooledByteBufferWriter(256);
            try
            {
                formatter.FormatTo(buffer);
                OtlpJsonIndexedExpander.WriteFromJson(ref writer, prefix, buffer.WrittenSpan);
            }
            finally
            {
                buffer.Dispose();
            }
            return;
        }

        string s = value?.ToString() ?? string.Empty;
        OtlpAttributeFormatter.WriteString(ref writer, prefix, s.AsSpan(), LogAttributeFieldNumber);
    }
}
