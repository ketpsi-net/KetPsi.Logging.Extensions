using System.Buffers;
using System.Text;

namespace KetPsi.Extensions.Logging.Abstractions;

public readonly struct LogParameter<T>(string? name, T value, string? format = null) : IDeferredUtf8Formatter
{
    public readonly string? Name = name;
    public readonly T Value = value;
    public readonly string? Format = format;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_alignBuffer;

    public void FormatTo(IBufferWriter<byte> writer) => LogValueFormatter.FormatTo(in Value, writer, Format);

    /// <summary>
    /// Writes the value padded to <paramref name="alignment"/> characters, following composite-format rules:
    /// positive values right-align, negative values left-align.
    /// </summary>
    public void FormatTo(IBufferWriter<byte> writer, int alignment)
    {
        if (alignment == 0)
        {
            FormatTo(writer);
            return;
        }

        // Format into a thread-local scratch buffer first so the padding can be measured.
        // Taking ownership (nulling the slot) keeps nested aligned formatting re-entrant.
        var scratch = t_alignBuffer ?? new ArrayBufferWriter<byte>(256);
        t_alignBuffer = null;
        try
        {
            scratch.Clear();
            FormatTo(scratch);

            ReadOnlySpan<byte> formatted = scratch.WrittenSpan;
            int padding = Math.Abs(alignment) - Encoding.UTF8.GetCharCount(formatted);

            if (alignment > 0) WritePadding(writer, padding);
            writer.Write(formatted);
            if (alignment < 0) WritePadding(writer, padding);
        }
        finally
        {
            t_alignBuffer = scratch;
        }
    }

    private static void WritePadding(IBufferWriter<byte> writer, int count)
    {
        if (count <= 0) return;
        Span<byte> span = writer.GetSpan(count);
        span[..count].Fill((byte)' ');
        writer.Advance(count);
    }

    public KeyValuePair<string, object?> ToKeyValuePair() => new(Name!, Value);
}
