using System.Buffers;

namespace KetPsi.Extensions.Logging.Abstractions;

public readonly struct MaskedString(string value, Index start, Index end) : ISpanFormattable, IDeferredUtf8Formatter
{
    private readonly string _value = value ?? string.Empty;
    private readonly Index _start = start.Value < 0 ? 0 : start;
    private readonly Index _end = end.Value < 0 ? 0 : end;

    // NEW: Direct-to-Buffer UTF-8 formatting
    public void FormatTo(IBufferWriter<byte> writer)
    {
        if (string.IsNullOrEmpty(_value)) return;

        int len = _value.Length;
        char[]? rented = null;

        // Fast path: stackalloc for strings under 256 chars (512 bytes)
        Span<char> charBuffer = len <= 256
            ? stackalloc char[len]
            : (rented = ArrayPool<char>.Shared.Rent(len));

        _value.AsSpan().CopyTo(charBuffer);
        ApplyMask(charBuffer[..len], len, _start, _end);

        // Encode directly to the UTF-8 writer without string allocation
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(len);
        int bytesWritten = System.Text.Encoding.UTF8.GetBytes(charBuffer[..len], writer.GetSpan(maxBytes));
        writer.Advance(bytesWritten);

        if (rented != null) ArrayPool<char>.Shared.Return(rented);
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < _value.Length)
        {
            charsWritten = 0;
            return false;
        }

        _value.AsSpan().CopyTo(destination);
        ApplyMask(destination, _value.Length, _start, _end);

        charsWritten = _value.Length;
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
    public override string ToString() => "MaskedString fallback";

    internal static void ApplyMask(Span<char> buffer, int length, Index start, Index end)
    {
        int actualStart = start.IsFromEnd ? length - start.Value : start.Value;
        int actualEnd = end.IsFromEnd ? length - end.Value : end.Value;

        actualStart = Math.Max(0, Math.Min(actualStart, length));
        actualEnd = Math.Max(0, Math.Min(actualEnd, length));

        if (actualStart < actualEnd)
        {
            buffer[actualStart..actualEnd].Fill('*');
        }
    }
}