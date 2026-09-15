using System.Buffers;
using System.Runtime.CompilerServices;

using KetPsi.Extensions.Logging.Abstractions;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal struct OtlpParameterSizeCalculator : ILogParameterWriter
{
    public int TotalSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in LogParameter<T> parameter)
    {
        ReadOnlySpan<char> nameSpan = parameter.Name.AsSpan();
        Type type = typeof(T);

        // 1. Primitive Unboxed Types FIRST (Prevents them falling into ISpanFormattable string serialization)
        if (type == typeof(long))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, long>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(int))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, int>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(short))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, short>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(byte))
        {
            TotalSize += OtlpAttributeFormatter.CalculateInt64Size(nameSpan, Unsafe.As<T, byte>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(double))
        {
            TotalSize += OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, double>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(float))
        {
            TotalSize += OtlpAttributeFormatter.CalculateDoubleSize(nameSpan, Unsafe.As<T, float>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(bool))
        {
            TotalSize += OtlpAttributeFormatter.CalculateBoolSize(nameSpan, Unsafe.As<T, bool>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref Unsafe.AsRef(in parameter.Value));
            TotalSize += OtlpAttributeFormatter.CalculateStringSize(nameSpan, s.AsSpan());
        }
        else
        {
            // 2. Safely route Guid, MaskedSpanFormattable, and JsonLogValue to the dispatcher
            var helper = FormatterDispatcher<T>.Helper;
            if (helper != null)
            {
                TotalSize += helper.CalculateSize(nameSpan, ref Unsafe.AsRef(in parameter.Value));
            }
            else
            {
                TotalSize += OtlpAttributeFormatter.CalculateStringSize(nameSpan, parameter.Value is null ? [] : parameter.Value.ToString().AsSpan());
            }
        }
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
    // Evaluated EXACTLY ONCE per generic type T (Guid, MaskedSpanFormattable<string>, etc)
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
    private readonly byte* _ptr = ptr;
    private readonly int _length = length;
    private int _offset = offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in LogParameter<T> parameter)
    {
        Span<byte> span = new Span<byte>(_ptr + _offset, _length - _offset);
        var writer = new ZeroAllocProtobufWriter(span);
        ReadOnlySpan<char> nameSpan = parameter.Name.AsSpan();
        Type type = typeof(T);

        if (type == typeof(long))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, long>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(int))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, int>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(short))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, short>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(byte))
        {
            OtlpAttributeFormatter.WriteInt64(ref writer, nameSpan, Unsafe.As<T, byte>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(double))
        {
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, double>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(float))
        {
            OtlpAttributeFormatter.WriteDouble(ref writer, nameSpan, Unsafe.As<T, float>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(bool))
        {
            OtlpAttributeFormatter.WriteBool(ref writer, nameSpan, Unsafe.As<T, bool>(ref Unsafe.AsRef(in parameter.Value)));
        }
        else if (type == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref Unsafe.AsRef(in parameter.Value));
            OtlpAttributeFormatter.WriteString(ref writer, nameSpan, s.AsSpan());
        }
        else
        {
            var helper = FormatterDispatcher<T>.Helper;
            if (helper != null)
            {
                helper.Write(ref writer, nameSpan, ref Unsafe.AsRef(in parameter.Value));
            }
            else
            {
                OtlpAttributeFormatter.WriteString(ref writer, nameSpan, parameter.Value is null ? [] : parameter.Value.ToString().AsSpan());
            }
        }

        _offset += writer.BytesWritten;
    }
}