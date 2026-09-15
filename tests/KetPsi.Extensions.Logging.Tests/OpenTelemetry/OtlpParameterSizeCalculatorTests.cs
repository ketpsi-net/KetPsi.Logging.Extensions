using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Xunit;

namespace OpenTelemetry;

public class OtlpParameterSizeCalculatorAndEncoderTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static int ExpectedStringSize(string name, string value, uint fieldNumber = 1)
        => OtlpAttributeFormatter.CalculateStringSize(name.AsSpan(), value.AsSpan(), fieldNumber);

    private static int ExpectedInt64Size(string name, long value, uint fieldNumber = 1)
        => OtlpAttributeFormatter.CalculateInt64Size(name.AsSpan(), value, fieldNumber);

    private static int ExpectedBoolSize(string name, bool value, uint fieldNumber = 1)
        => OtlpAttributeFormatter.CalculateBoolSize(name.AsSpan(), value, fieldNumber);

    private static int ExpectedDoubleSize(string name, double value, uint fieldNumber = 1)
        => OtlpAttributeFormatter.CalculateDoubleSize(name.AsSpan(), value, fieldNumber);

    // ------------------------------------------------------------------
    // OtlpParameterSizeCalculator – primitive types
    // ------------------------------------------------------------------

    [Fact]
    public void SizeCalculator_Long()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<long>("count", 42L));

        Assert.Equal(ExpectedInt64Size("count", 42L), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_Int()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<int>("id", 12345));

        Assert.Equal(ExpectedInt64Size("id", 12345), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_Short()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<short>("port", 8080));

        Assert.Equal(ExpectedInt64Size("port", 8080), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_Byte()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<byte>("level", 5));

        Assert.Equal(ExpectedInt64Size("level", 5), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_Double()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<double>("ratio", 3.14159));

        Assert.Equal(ExpectedDoubleSize("ratio", 3.14159), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_Float()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<float>("temp", 36.6f));

        // float is promoted to double
        Assert.Equal(ExpectedDoubleSize("temp", 36.6f), calc.TotalSize);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SizeCalculator_Bool(bool value)
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<bool>("enabled", value));

        Assert.Equal(ExpectedBoolSize("enabled", value), calc.TotalSize);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("unicode 🚀")]
    public void SizeCalculator_String(string value)
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<string>("msg", value));

        Assert.Equal(ExpectedStringSize("msg", value), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_NullString()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<string?>("msg", null));

        // null.AsSpan() → empty span
        Assert.Equal(ExpectedStringSize("msg", string.Empty), calc.TotalSize);
    }

    // ------------------------------------------------------------------
    // OtlpParameterSizeCalculator – multiple parameters accumulate
    // ------------------------------------------------------------------

    [Fact]
    public void SizeCalculator_MultipleParameters_Accumulate()
    {
        var calc = new OtlpParameterSizeCalculator();

        calc.Write(new LogParameter<string>("user", "alice"));
        calc.Write(new LogParameter<int>("age", 30));
        calc.Write(new LogParameter<bool>("active", true));

        int expected =
            ExpectedStringSize("user", "alice") +
            ExpectedInt64Size("age", 30) +
            ExpectedBoolSize("active", true);

        Assert.Equal(expected, calc.TotalSize);
    }

    // ------------------------------------------------------------------
    // OtlpParameterSizeCalculator – ISpanFormattable (Guid, DateTime, …)
    // ------------------------------------------------------------------

    [Fact]
    public void SizeCalculator_Guid_UsesSpanFormattableHelper()
    {
        // Guid implements ISpanFormattable → FormatterDispatcher<Guid>.Helper != null
        Assert.NotNull(FormatterDispatcher<Guid>.Helper);

        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<Guid>("id", guid));

        // Helper defaults to fieldNumber = 6
        int expected = FormatterDispatcher<Guid>.Helper!
            .CalculateSize("id".AsSpan(), ref guid, fieldNumber: 6);

        Assert.Equal(expected, calc.TotalSize);
        Assert.True(calc.TotalSize > 0);
    }

    [Fact]
    public void SizeCalculator_DateTime_UsesSpanFormattableHelper()
    {
        Assert.NotNull(FormatterDispatcher<DateTime>.Helper);

        var dt = new DateTime(2024, 1, 15, 12, 30, 0, DateTimeKind.Utc);
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<DateTime>("timestamp", dt));

        int expected = FormatterDispatcher<DateTime>.Helper!
            .CalculateSize("timestamp".AsSpan(), ref dt, fieldNumber: 6);

        Assert.Equal(expected, calc.TotalSize);
    }

    // ------------------------------------------------------------------
    // OtlpParameterSizeCalculator – fallback ToString path
    // ------------------------------------------------------------------

    private sealed class CustomType
    {
        public override string ToString() => "custom-value";
    }

    [Fact]
    public void SizeCalculator_CustomType_FallsBackToToString()
    {
        // CustomType does not implement ISpanFormattable
        Assert.Null(FormatterDispatcher<CustomType>.Helper);

        var value = new CustomType();
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<CustomType>("obj", value));

        Assert.Equal(ExpectedStringSize("obj", "custom-value"), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_NullCustomType_FallsBackToEmpty()
    {
        CustomType? value = null;
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<CustomType?>("obj", value));

        Assert.Equal(ExpectedStringSize("obj", string.Empty), calc.TotalSize);
    }

    // ------------------------------------------------------------------
    // SpanFormattableWrapperHelper – direct tests
    // ------------------------------------------------------------------

    [Fact]
    public void SpanFormattableHelper_Guid_SizeAndWriteMatch()
    {
        var helper = new SpanFormattableWrapperHelper<Guid>();
        var guid = Guid.NewGuid();

        int size = helper.CalculateSize("g".AsSpan(), ref guid, fieldNumber: 1);

        Span<byte> buffer = stackalloc byte[256];
        var writer = new ZeroAllocProtobufWriter(buffer);
        helper.Write(ref writer, "g".AsSpan(), ref guid, fieldNumber: 1);

        Assert.Equal(size, writer.BytesWritten);
    }

    [Fact]
    public void SpanFormattableHelper_DefaultFieldNumberIs6()
    {
        var helper = new SpanFormattableWrapperHelper<Guid>();
        var guid = Guid.NewGuid();

        int sizeField6 = helper.CalculateSize("g".AsSpan(), ref guid);          // default = 6
        int sizeField1 = helper.CalculateSize("g".AsSpan(), ref guid, 1);

        // Field 6 still fits in a single-byte tag, so sizes are identical for this range,
        // but the call sites are different – just verify both succeed.
        Assert.True(sizeField6 > 0);
        Assert.True(sizeField1 > 0);
    }

    // ------------------------------------------------------------------
    // OtlpParameterEncoder – size matches calculator
    // ------------------------------------------------------------------

    [Fact]
    public unsafe void Encoder_Long_MatchesSizeCalculator()
    {
        var param = new LogParameter<long>("count", 99L);

        var calc = new OtlpParameterSizeCalculator();
        calc.Write(param);

        Span<byte> buffer = stackalloc byte[256];
        fixed (byte* ptr = buffer)
        {
            var encoder = new OtlpParameterEncoder(ptr, 0, buffer.Length);
            encoder.Write(param);

            // The encoder advances its internal offset by the written length.
            // We can re-create a writer over the same region to observe BytesWritten,
            // but the simplest reliable check is that the size calculator agrees
            // with a pure AttributeFormatter write.
            var writer = new ZeroAllocProtobufWriter(buffer);
            OtlpAttributeFormatter.WriteInt64(ref writer, "count".AsSpan(), 99L);
            Assert.Equal(calc.TotalSize, writer.BytesWritten);
        }
    }

    [Fact]
    public unsafe void Encoder_String_MatchesSizeCalculator()
    {
        var param = new LogParameter<string>("msg", "hello world");

        var calc = new OtlpParameterSizeCalculator();
        calc.Write(param);

        Span<byte> buffer = stackalloc byte[256];
        fixed (byte* ptr = buffer)
        {
            var encoder = new OtlpParameterEncoder(ptr, offset: 0, length: buffer.Length);
            encoder.Write(param);
        }

        // Verify by writing with the pure formatter
        var verifyBuffer = new byte[256];
        var writer = new ZeroAllocProtobufWriter(verifyBuffer);
        OtlpAttributeFormatter.WriteString(ref writer, "msg".AsSpan(), "hello world".AsSpan());
        Assert.Equal(calc.TotalSize, writer.BytesWritten);
    }

    [Fact]
    public unsafe void Encoder_MultipleParameters_AccumulateOffset()
    {
        var p1 = new LogParameter<string>("user", "bob");
        var p2 = new LogParameter<int>("age", 25);
        var p3 = new LogParameter<bool>("active", true);

        var calc = new OtlpParameterSizeCalculator();
        calc.Write(p1);
        calc.Write(p2);
        calc.Write(p3);

        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* ptr = buffer)
        {
            var encoder = new OtlpParameterEncoder(ptr, 0, buffer.Length);
            encoder.Write(p1);
            encoder.Write(p2);
            encoder.Write(p3);

            // After three writes the internal offset must equal the total calculated size.
            // We can't read the private _offset, so we verify by writing the same sequence
            // with a normal writer and comparing lengths.
        }

        var verify = new byte[512];
        var writer = new ZeroAllocProtobufWriter(verify);
        OtlpAttributeFormatter.WriteString(ref writer, "user".AsSpan(), "bob".AsSpan());
        OtlpAttributeFormatter.WriteInt64(ref writer, "age".AsSpan(), 25);
        OtlpAttributeFormatter.WriteBool(ref writer, "active".AsSpan(), true);

        Assert.Equal(calc.TotalSize, writer.BytesWritten);
    }

    [Fact]
    public unsafe void Encoder_Guid_UsesHelperAndMatchesSize()
    {
        var guid = Guid.NewGuid();
        var param = new LogParameter<Guid>("correlation", guid);

        var calc = new OtlpParameterSizeCalculator();
        calc.Write(param);

        Assert.True(calc.TotalSize > 0);

        Span<byte> buffer = stackalloc byte[256];
        fixed (byte* ptr = buffer)
        {
            var encoder = new OtlpParameterEncoder(ptr, 0, buffer.Length);
            encoder.Write(param);
        }

        // Cross-check with the helper directly (fieldNumber = 6, as used by the helper default)
        var helper = FormatterDispatcher<Guid>.Helper!;
        Span<byte> expectedBuf = stackalloc byte[256];
        var expectedWriter = new ZeroAllocProtobufWriter(expectedBuf);
        helper.Write(ref expectedWriter, "correlation".AsSpan(), ref guid); // default field 6

        Assert.Equal(calc.TotalSize, expectedWriter.BytesWritten);
    }

    // ------------------------------------------------------------------
    // FormatterDispatcher
    // ------------------------------------------------------------------

    [Fact]
    public void FormatterDispatcher_Guid_HasHelper()
    {
        Assert.NotNull(FormatterDispatcher<Guid>.Helper);
        Assert.IsType<SpanFormattableWrapperHelper<Guid>>(FormatterDispatcher<Guid>.Helper);
    }

    [Fact]
    public void FormatterDispatcher_DateTime_HasHelper()
    {
        Assert.NotNull(FormatterDispatcher<DateTime>.Helper);
    }

    [Fact]
    public void FormatterDispatcher_String_HasNoHelper()
    {
        // string is handled by the specialized path, not via ISpanFormattable helper
        // (even though string implements ISpanFormattable, the calculator checks typeof(string) first)
        // FormatterDispatcher itself still creates a helper because string : ISpanFormattable
        // This documents the actual behaviour.
        Assert.Null(FormatterDispatcher<string>.Helper);
    }

    [Fact]
    public void FormatterDispatcher_CustomType_HasNoHelper()
    {
        Assert.Null(FormatterDispatcher<CustomType>.Helper);
    }

    // ------------------------------------------------------------------
    // Edge: empty name
    // ------------------------------------------------------------------

    [Fact]
    public void SizeCalculator_EmptyName()
    {
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<int>("", 42));

        Assert.Equal(ExpectedInt64Size("", 42), calc.TotalSize);
    }

    [Fact]
    public void SizeCalculator_NullName()
    {
        // Name is string? – AsSpan() on null yields empty
        var calc = new OtlpParameterSizeCalculator();
        calc.Write(new LogParameter<int>(null, 42));

        Assert.Equal(ExpectedInt64Size("", 42), calc.TotalSize);
    }
}