using System.Buffers;

namespace KetPsi.Extensions.Logging.Abstractions;

public readonly struct MaskedSpanFormattable<T> : ISpanFormattable, IDeferredUtf8Formatter
{
    private const int StackCharLimit = 256;

    private readonly T _value;
    private readonly Index _start;
    private readonly Index _end;
    private readonly string? _format;

    public MaskedSpanFormattable(T value, Index start, Index end, string? format = null)
    {
        _value = value;
        _start = start;
        _end = end;
        _format = format;
    }

    public void FormatTo(IBufferWriter<byte> writer)
    {
        Span<char> charBuffer = stackalloc char[StackCharLimit];
        char[]? rented = null;
        try
        {
            int charsWritten;
            int size = StackCharLimit * 4;
            // Grow through the pool until the (boxing-free) formatter fits.
            while (!LogValueFormatter.TryFormat(in _value, charBuffer, out charsWritten, _format))
            {
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                if (size > (1 << 20))
                {
                    rented = null;
                    return;
                }
                rented = ArrayPool<char>.Shared.Rent(size);
                charBuffer = rented;
                size = rented.Length * 2;
            }

            Span<char> chars = charBuffer[..charsWritten];
            MaskedString.ApplyMask(chars, charsWritten, _start, _end);
            LogValueFormatter.WriteChars(writer, chars);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        // The format captured at construction wins; it is what the log template asked for.
        string? effectiveFormat = _format ?? (format.IsEmpty ? null : format.ToString());
        if (!LogValueFormatter.TryFormat(in _value, destination, out charsWritten, effectiveFormat))
            return false;

        MaskedString.ApplyMask(destination, charsWritten, _start, _end);
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
    public override string ToString() => "MaskedSpanFormattable fallback";
}
