using System;
using System.Buffers.Binary;
using System.Text;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Xunit;

namespace OpenTelemetry;

public class OtlpLogRecordFormatterTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Span<byte> CreateBuffer(int size = 8192) => new byte[size];

    private static void AssertSizeMatchesWritten(int calculatedSize, in ZeroAllocProtobufWriter writer)
    {
        Assert.Equal(calculatedSize, writer.BytesWritten);
    }

    // ------------------------------------------------------------------
    // CalculateLogRecordSize ↔ WriteLogRecord (attributesSize = 0)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0L, 0, "", "")]
    [InlineData(1_725_000_000_000_000_000L, 9, "INFO", "hello")]
    [InlineData(0L, 1, "TRACE", "trace message")]
    [InlineData(long.MaxValue, 21, "FATAL", "fatal error")]
    [InlineData(123456789L, 13, "WARN", "warning with spaces")]
    public void CalculateLogRecordSize_MatchesWrite_WhenNoIdsAndNoAttributes(
        long timeUnixNano,
        int severityNumber,
        string severityText,
        string body)
    {
        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            timeUnixNano,
            severityNumber,
            severityText,
            body.AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer,
            timeUnixNano,
            severityNumber,
            severityText,
            body.AsSpan(),
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateLogRecordSize_MatchesWrite_WithTraceAndSpanId()
    {
        long time = 1_700_000_000_000_000_000L;
        int severity = 9;
        string text = "INFO";
        string body = "request completed";

        // Typical 16-byte TraceId + 8-byte SpanId
        Span<byte> traceId = stackalloc byte[16];
        Span<byte> spanId = stackalloc byte[8];
        for (int i = 0; i < 16; i++) traceId[i] = (byte)(i + 1);
        for (int i = 0; i < 8; i++) spanId[i] = (byte)(0xA0 + i);

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            time, severity, text, body.AsSpan(),
            attributesSize: 0,
            traceId, spanId);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, time, severity, text, body.AsSpan(),
            traceId, spanId);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateLogRecordSize_MatchesWrite_OnlyTraceId()
    {
        Span<byte> traceId = stackalloc byte[16];
        traceId.Fill(0x42);

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", "body".AsSpan(),
            attributesSize: 0,
            traceId, ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "body".AsSpan(),
            traceId, ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateLogRecordSize_MatchesWrite_OnlySpanId()
    {
        Span<byte> spanId = stackalloc byte[8];
        spanId.Fill(0xAB);

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", "body".AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, spanId);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "body".AsSpan(),
            ReadOnlySpan<byte>.Empty, spanId);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // attributesSize is simply added (not written by WriteLogRecord)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(100)]
    [InlineData(1024)]
    public void CalculateLogRecordSize_IncludesAttributesSize(int attributesSize)
    {
        int baseSize = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", "body".AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        int withAttrs = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", "body".AsSpan(),
            attributesSize,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        Assert.Equal(baseSize + attributesSize, withAttrs);
    }

    // ------------------------------------------------------------------
    // Empty / edge-case strings
    // ------------------------------------------------------------------

    [Fact]
    public void CalculateAndWrite_EmptySeverityTextAndBody()
    {
        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            123L, 0, "", ReadOnlySpan<char>.Empty,
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 123L, 0, "", ReadOnlySpan<char>.Empty,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
        Assert.True(size > 0);
    }

    [Fact]
    public void CalculateAndWrite_UnicodeBodyAndSeverityText()
    {
        string severityText = "ИНФО";
        string body = "メッセージ 🚀";

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            1L, 9, severityText, body.AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer(512);
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 1L, 9, severityText, body.AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateAndWrite_LongBody()
    {
        string body = new string('x', 5_000);

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", body.AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer(size + 64);
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", body.AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // Severity number boundaries
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(24)]          // highest defined in OTLP
    [InlineData(127)]
    [InlineData(128)]         // forces 2-byte varint
    [InlineData(int.MaxValue)]
    public void CalculateAndWrite_VariousSeverityNumbers(int severityNumber)
    {
        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, severityNumber, "S", "b".AsSpan(),
            attributesSize: 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, severityNumber, "S", "b".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // Wire-format tag verification
    // ------------------------------------------------------------------

    [Fact]
    public void WriteLogRecord_EmitsCorrectFixedTagsForTimeFields()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0x1122334455667788L, 9, "INFO", "body".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        // Field 1: time_unix_nano → tag 0x09
        Assert.Equal(0x09, buffer[0]);

        // Field 11: observed_time_unix_nano → tag 0x59
        // It appears right after the first 9-byte time field
        Assert.Equal(0x59, buffer[9]);
    }

    [Fact]
    public void WriteLogRecord_EmitsCorrectTimePayload()
    {
        const long time = 0x0102030405060708L;

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, time, 9, "INFO", "b".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        // Bytes 1..8 should be the little-endian representation of time
        Span<byte> expected = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(expected, time);

        Assert.True(buffer.Slice(1, 8).SequenceEqual(expected));
        // observed_time uses the same value
        Assert.True(buffer.Slice(10, 8).SequenceEqual(expected));
    }

    [Fact]
    public void WriteLogRecord_EmitsSeverityNumberTag()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "b".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        // Tag for field 2, wire type 0 = (2 << 3) | 0 = 0x10
        bool found = false;
        for (int i = 0; i < writer.BytesWritten; i++)
        {
            if (buffer[i] == 0x10)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected severity_number tag 0x10 not found");
    }

    [Fact]
    public void WriteLogRecord_EmitsBodyAnyValueTag()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "hello".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        // Field 5, wire type 2 = (5 << 3) | 2 = 0x2A
        Assert.Contains((byte)0x2A, buffer[..writer.BytesWritten].ToArray());
    }

    [Fact]
    public void WriteLogRecord_EmitsTraceIdAndSpanIdTagsWhenPresent()
    {
        Span<byte> traceId = stackalloc byte[16];
        Span<byte> spanId = stackalloc byte[8];
        traceId.Fill(1);
        spanId.Fill(2);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "b".AsSpan(),
            traceId, spanId);

        // Field 9, wire type 2 = (9 << 3) | 2 = 0x4A
        // Field 10, wire type 2 = (10 << 3) | 2 = 0x52
        var written = buffer[..writer.BytesWritten].ToArray();
        Assert.Contains((byte)0x4A, written);
        Assert.Contains((byte)0x52, written);
    }

    [Fact]
    public void WriteLogRecord_OmitsTraceIdAndSpanIdWhenEmpty()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "b".AsSpan(),
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var written = buffer[..writer.BytesWritten].ToArray();
        Assert.DoesNotContain((byte)0x4A, written); // no trace_id tag
        Assert.DoesNotContain((byte)0x52, written); // no span_id tag
    }

    // ------------------------------------------------------------------
    // Size purity
    // ------------------------------------------------------------------

    [Fact]
    public void CalculateLogRecordSize_IsPure()
    {
        int s1 = OtlpLogRecordFormatter.CalculateLogRecordSize(
            42, 9, "INFO", "body".AsSpan(), 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        int s2 = OtlpLogRecordFormatter.CalculateLogRecordSize(
            42, 9, "INFO", "body".AsSpan(), 0,
            ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        Assert.Equal(s1, s2);
    }

    // ------------------------------------------------------------------
    // Large IDs (should still match)
    // ------------------------------------------------------------------

    [Fact]
    public void CalculateAndWrite_NonStandardIdLengths()
    {
        // OTLP expects 16/8, but the formatter is length-agnostic
        Span<byte> shortTrace = stackalloc byte[4] { 1, 2, 3, 4 };
        Span<byte> longSpan = stackalloc byte[12];
        longSpan.Fill(0xFF);

        int size = OtlpLogRecordFormatter.CalculateLogRecordSize(
            0, 9, "INFO", "b".AsSpan(),
            attributesSize: 0,
            shortTrace, longSpan);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpLogRecordFormatter.WriteLogRecord(
            ref writer, 0, 9, "INFO", "b".AsSpan(),
            shortTrace, longSpan);

        AssertSizeMatchesWritten(size, writer);
    }
}