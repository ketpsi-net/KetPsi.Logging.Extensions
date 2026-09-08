using System.Buffers;
using System.Runtime.CompilerServices;

namespace KetPsi.Extensions.Logging.Abstractions;

public readonly struct MaskedSpanFormattable<T> : ISpanFormattable, IDeferredUtf8Formatter
{
    private readonly T _value;
    private readonly Index _start;
    private readonly Index _end;

    public MaskedSpanFormattable(T value, Index start, Index end)
    {
        _value = value;
        _start = start;
        _end = end;
    }

    public void FormatTo(IBufferWriter<byte> writer)
    {
        char[]? rented = null;
        Span<char> charBuffer = stackalloc char[256];
        int charsWritten = 0;
        bool success = false;

        // 1. Direct String Path (Zero-Boxing via JIT and Unsafe)
        if (typeof(T) == typeof(string))
        {
            // Bypasses the 'is string' type-check and strictly avoids 'object' casting
            string s = Unsafe.As<T, string>(ref Unsafe.AsRef(in _value));
            int len = s.Length;
            charBuffer = len <= 256 ? stackalloc char[len] : (rented = ArrayPool<char>.Shared.Rent(len));
            s.AsSpan().CopyTo(charBuffer);
            charsWritten = len;
            success = true;
        }
        // 2. ISpanFormattable Path (Zero-Boxing via Static Delegate Constraint)
        else if (MaskFormatterCache<T>.CharFormatter != null)
        {
            if (!MaskFormatterCache<T>.CharFormatter(_value, charBuffer, out charsWritten, default, null))
            {
                // Fallback for huge formatted structs
                string fallbackStr = _value?.ToString() ?? string.Empty;
                int len = fallbackStr.Length;
                charBuffer = len <= 256 ? stackalloc char[len] : (rented = ArrayPool<char>.Shared.Rent(len));
                fallbackStr.AsSpan().CopyTo(charBuffer);
                charsWritten = len;
            }
            success = true;
        }

        // 3. Fallback for everything else
        if (!success)
        {
            string fallbackStr = _value?.ToString() ?? string.Empty;
            int len = fallbackStr.Length;
            charBuffer = len <= 256 ? stackalloc char[len] : (rented = ArrayPool<char>.Shared.Rent(len));
            fallbackStr.AsSpan().CopyTo(charBuffer);
            charsWritten = len;
        }

        // Apply the mask over the written characters
        MaskedString.ApplyMask(charBuffer[..charsWritten], charsWritten, _start, _end);

        // Encode straight to the UTF-8 byte writer
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(charsWritten);
        int bytesWritten = System.Text.Encoding.UTF8.GetBytes(charBuffer[..charsWritten], writer.GetSpan(maxBytes));
        writer.Advance(bytesWritten);

        if (rented != null) ArrayPool<char>.Shared.Return(rented);
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (typeof(T) == typeof(string))
        {
            string s = Unsafe.As<T, string>(ref Unsafe.AsRef(in _value));
            if (s.Length > destination.Length)
            {
                charsWritten = 0;
                return false;
            }
            s.AsSpan().CopyTo(destination);
            charsWritten = s.Length;
        }
        else if (MaskFormatterCache<T>.CharFormatter != null)
        {
            if (!MaskFormatterCache<T>.CharFormatter(_value, destination, out charsWritten, format, provider))
            {
                return false;
            }
        }
        else
        {
            string fallbackStr = _value?.ToString() ?? string.Empty;
            if (fallbackStr.Length > destination.Length)
            {
                charsWritten = 0;
                return false;
            }
            fallbackStr.AsSpan().CopyTo(destination);
            charsWritten = fallbackStr.Length;
        }

        MaskedString.ApplyMask(destination, charsWritten, _start, _end);
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
    public override string ToString() => "MaskedSpanFormattable fallback";
}

// -------------------------------------------------------------
// The Static Cache that prevents Interface Boxing
// -------------------------------------------------------------
internal static class MaskFormatterCache<T>
{
    public delegate bool CharDelegate(T value, Span<char> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov);
    public static readonly CharDelegate? CharFormatter;

    static MaskFormatterCache()
    {
        if (typeof(ISpanFormattable).IsAssignableFrom(typeof(T)))
        {
            var method = typeof(MaskFormatterCache<T>)
                .GetMethod(nameof(FormatChar), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(T));

            CharFormatter = (CharDelegate)Delegate.CreateDelegate(typeof(CharDelegate), method);
        }
    }

    // This method is constrained to TActual : ISpanFormattable
    // Calling TryFormat here is a direct struct call. It completely skips boxing!
    private static bool FormatChar<TActual>(TActual value, Span<char> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov) where TActual : ISpanFormattable
    {
        return value.TryFormat(dest, out written, fmt, prov);
    }
}