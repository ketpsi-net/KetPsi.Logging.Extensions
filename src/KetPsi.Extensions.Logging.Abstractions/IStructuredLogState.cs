namespace KetPsi.Extensions.Logging.Abstractions;

public interface IStructuredLogState
{
    ReadOnlySpan<char> OriginalFormat { get; }

    /// <summary>The message template pre-encoded as UTF-8, so sinks never transcode it per log.</summary>
    ReadOnlySpan<byte> OriginalFormatUtf8 { get; }

    void WriteParameters<TWriter>(ref TWriter writer) where TWriter : struct, ILogParameterWriter;

    /// <summary>Number of parameters passed to <see cref="WriteParameters{TWriter}"/>.</summary>
    int ParameterCount { get; }

    /// <summary>
    /// Renders the message like <see cref="IDeferredUtf8Formatter.FormatTo"/> and records, for parameter <c>i</c>,
    /// its byte offset at <c>valueRanges[2 * i]</c> and its length at <c>valueRanges[2 * i + 1]</c>.
    /// An offset of <c>-1</c> marks bytes that are not the plain value (alignment padding), which must not be reused.
    /// <paramref name="valueRanges"/> holds at least <c>2 * ParameterCount</c> entries.
    /// </summary>
    void FormatTo(IPositionedBufferWriter writer, int[] valueRanges);
}
