using System.Buffers;
using System.Text.Unicode;

namespace KetPsi.Extensions.Logging.Console.Internals.Formatters;

/// <summary>
/// Writes an exception's text straight into a formatter's UTF-8 buffer.
/// </summary>
/// <remarks>
/// <see cref="Exception.ToString"/> is the one allocation, and it is unavoidable: it is the only rendering that
/// includes what derived exceptions add (an AggregateException's inner list, extra data on others), and the stack
/// trace text inside it is built on every call. Everything after that is copy-free. The text is transcoded in
/// bounded chunks, so the thread's reusable output buffer grows by roughly the exception's length rather than
/// the 3x worst case a single <c>GetMaxByteCount</c> request would demand.
/// </remarks>
internal static class ExceptionWriter
{
    private const int ChunkChars = 1024;
    private const int MaxBytesPerChar = 3;

    /// <summary>Writes the exception as-is (multi-line).</summary>
    public static void Write(IBufferWriter<byte> writer, Exception exception)
        => WriteChars(writer, exception.ToString());

    /// <summary>
    /// Writes the exception on one line, replacing each line break (<c>\r\n</c>, <c>\n</c> or <c>\r</c>) with a
    /// space, for line-oriented sinks such as systemd/journald where every newline starts a new entry.
    /// </summary>
    public static void WriteSingleLine(IBufferWriter<byte> writer, Exception exception)
    {
        ReadOnlySpan<char> remaining = exception.ToString();
        while (!remaining.IsEmpty)
        {
            int lineBreak = remaining.IndexOfAny('\r', '\n');
            if (lineBreak < 0)
            {
                WriteChars(writer, remaining);
                return;
            }

            WriteChars(writer, remaining[..lineBreak]);
            writer.Write(" "u8);

            bool crlf = remaining[lineBreak] == '\r' && lineBreak + 1 < remaining.Length && remaining[lineBreak + 1] == '\n';
            remaining = remaining[(lineBreak + (crlf ? 2 : 1))..];
        }
    }

    private static void WriteChars(IBufferWriter<byte> writer, ReadOnlySpan<char> chars)
    {
        while (!chars.IsEmpty)
        {
            int take = Math.Min(chars.Length, ChunkChars);
            Span<byte> destination = writer.GetSpan(take * MaxBytesPerChar);

            // isFinalBlock: false leaves a high surrogate split at the chunk edge for the next chunk.
            Utf8.FromUtf16(chars[..take], destination, out int charsRead, out int bytesWritten, isFinalBlock: take == chars.Length);
            writer.Advance(bytesWritten);
            chars = chars[charsRead..];
        }
    }
}
