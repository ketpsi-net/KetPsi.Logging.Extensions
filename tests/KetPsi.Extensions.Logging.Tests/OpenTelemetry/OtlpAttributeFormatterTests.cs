using System.Buffers.Binary;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

namespace OpenTelemetry;

public class OtlpAttributeFormatterTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Span<byte> CreateBuffer(int size = 4096) => new byte[size];

    private static void AssertSizeMatchesWritten(int calculatedSize, in ZeroAllocProtobufWriter writer)
    {
        Assert.Equal(calculatedSize, writer.BytesWritten);
    }

    // ------------------------------------------------------------------
    // CalculateStringSize ↔ WriteString
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("k", "v")]
    [InlineData("key", "value")]
    [InlineData("a", "")]
    [InlineData("", "b")]
    [InlineData("service.name", "my-service")]
    [InlineData("http.method", "GET")]
    [InlineData("http.status_code", "200")]
    public void CalculateStringSize_MatchesWriteString(string key, string value)
    {
        int size = OtlpAttributeFormatter.CalculateStringSize(key.AsSpan(), value.AsSpan());

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, key.AsSpan(), value.AsSpan());

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateStringSize_EmptyKeyAndValue()
    {
        int size = OtlpAttributeFormatter.CalculateStringSize(
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer,
            ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

        AssertSizeMatchesWritten(size, writer);
        Assert.True(size > 0);
    }

    [Fact]
    public void CalculateStringSize_UnicodeAndMultiByteUtf8()
    {
        // Cyrillic + CJK + emoji (all multi-byte UTF-8)
        string key = "пользователь";
        string value = "こんにちは 🌍";

        int size = OtlpAttributeFormatter.CalculateStringSize(key.AsSpan(), value.AsSpan());

        var buffer = CreateBuffer(512);
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, key.AsSpan(), value.AsSpan());

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateStringSize_LongValue()
    {
        string key = "long";
        string value = new string('x', 8_000);

        int size = OtlpAttributeFormatter.CalculateStringSize(key.AsSpan(), value.AsSpan());

        var buffer = CreateBuffer(size + 64);
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, key.AsSpan(), value.AsSpan());

        AssertSizeMatchesWritten(size, writer);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(15u)]
    [InlineData(16u)]   // forces 2-byte tag
    [InlineData(100u)]
    [InlineData(127u)]
    [InlineData(128u)]
    public void CalculateStringSize_CustomFieldNumber(uint fieldNumber)
    {
        const string key = "k";
        const string value = "v";

        int size = OtlpAttributeFormatter.CalculateStringSize(
            key.AsSpan(), value.AsSpan(), fieldNumber);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer,
            key.AsSpan(), value.AsSpan(), fieldNumber);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // CalculateInt64Size ↔ WriteInt64
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void CalculateInt64Size_MatchesWriteInt64(long value)
    {
        const string key = "int.key";

        int size = OtlpAttributeFormatter.CalculateInt64Size(key.AsSpan(), value);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteInt64(ref writer, key.AsSpan(), value);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateInt64Size_EmptyKey()
    {
        int size = OtlpAttributeFormatter.CalculateInt64Size(ReadOnlySpan<char>.Empty, 123L);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteInt64(ref writer, ReadOnlySpan<char>.Empty, 123L);

        AssertSizeMatchesWritten(size, writer);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(16u)]
    [InlineData(128u)]
    public void CalculateInt64Size_CustomFieldNumber(uint fieldNumber)
    {
        int size = OtlpAttributeFormatter.CalculateInt64Size("k".AsSpan(), 99L, fieldNumber);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteInt64(ref writer, "k".AsSpan(), 99L, fieldNumber);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // CalculateBoolSize ↔ WriteBool
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CalculateBoolSize_MatchesWriteBool(bool value)
    {
        const string key = "bool.key";

        int size = OtlpAttributeFormatter.CalculateBoolSize(key.AsSpan(), value);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteBool(ref writer, key.AsSpan(), value);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateBoolSize_EmptyKey()
    {
        int size = OtlpAttributeFormatter.CalculateBoolSize(ReadOnlySpan<char>.Empty, true);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteBool(ref writer, ReadOnlySpan<char>.Empty, true);

        AssertSizeMatchesWritten(size, writer);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(15u)]
    [InlineData(16u)]
    [InlineData(128u)]
    public void CalculateBoolSize_CustomFieldNumber(uint fieldNumber)
    {
        int size = OtlpAttributeFormatter.CalculateBoolSize("b".AsSpan(), false, fieldNumber);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteBool(ref writer, "b".AsSpan(), false, fieldNumber);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // CalculateDoubleSize ↔ WriteDouble
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.141592653589793)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void CalculateDoubleSize_MatchesWriteDouble(double value)
    {
        const string key = "dbl.key";

        int size = OtlpAttributeFormatter.CalculateDoubleSize(key.AsSpan(), value);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteDouble(ref writer, key.AsSpan(), value);

        AssertSizeMatchesWritten(size, writer);
    }

    [Fact]
    public void CalculateDoubleSize_EmptyKey()
    {
        int size = OtlpAttributeFormatter.CalculateDoubleSize(ReadOnlySpan<char>.Empty, 1.23);

        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteDouble(ref writer, ReadOnlySpan<char>.Empty, 1.23);

        AssertSizeMatchesWritten(size, writer);
    }

    // ------------------------------------------------------------------
    // Wire-format content verification
    // ------------------------------------------------------------------

    [Fact]
    public void WriteString_ProducesCorrectOuterTag()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, "a".AsSpan(), "b".AsSpan());

        // Outer KeyValue field number 1, wire type 2 → tag = (1 << 3) | 2 = 0x0A
        Assert.Equal(0x0A, buffer[0]);
    }

    [Fact]
    public void WriteInt64_ProducesCorrectAnyValueIntTag()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteInt64(ref writer, "x".AsSpan(), 42L);

        // AnyValue.int_value = field 3, wire type 0 → tag = 0x18
        bool found = false;
        for (int i = 0; i < writer.BytesWritten; i++)
        {
            if (buffer[i] == 0x18)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected tag 0x18 (AnyValue.int_value) was not found");
    }

    [Fact]
    public void WriteBool_ProducesCorrectTagAndValue()
    {
        // true
        {
            var buffer = CreateBuffer();
            var writer = new ZeroAllocProtobufWriter(buffer);
            OtlpAttributeFormatter.WriteBool(ref writer, "b".AsSpan(), true);

            // AnyValue.bool_value = field 2, wire type 0 → tag 0x10
            Assert.Contains((byte)0x10, buffer[..writer.BytesWritten].ToArray());
            Assert.Contains((byte)0x01, buffer[..writer.BytesWritten].ToArray());
        }

        // false
        {
            var buffer = CreateBuffer();
            var writer = new ZeroAllocProtobufWriter(buffer);
            OtlpAttributeFormatter.WriteBool(ref writer, "b".AsSpan(), false);

            Assert.Contains((byte)0x10, buffer[..writer.BytesWritten].ToArray());
            Assert.Contains((byte)0x00, buffer[..writer.BytesWritten].ToArray());
        }
    }

    [Fact]
    public void WriteDouble_ProducesCorrectTagAndFixed64Payload()
    {
        const double value = 1.5;
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteDouble(ref writer, "d".AsSpan(), value);

        // AnyValue.double_value = field 4, wire type 1 → tag = 0x21
        Assert.Contains((byte)0x21, buffer[..writer.BytesWritten].ToArray());

        // Verify the 8-byte little-endian IEEE-754 representation
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        Span<byte> expected = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(expected, bits);

        bool found = false;
        for (int i = 0; i <= writer.BytesWritten - 8; i++)
        {
            if (buffer.Slice(i, 8).SequenceEqual(expected))
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Fixed64 payload for the double value was not found");
    }

    // ------------------------------------------------------------------
    // Size purity / stability
    // ------------------------------------------------------------------

    [Fact]
    public void CalculateMethods_ArePure_AndConsistent()
    {
        const string key = "stable";
        const string strVal = "value";
        const long intVal = 12345;
        const bool boolVal = true;
        const double dblVal = Math.PI;

        Assert.Equal(
            OtlpAttributeFormatter.CalculateStringSize(key.AsSpan(), strVal.AsSpan()),
            OtlpAttributeFormatter.CalculateStringSize(key.AsSpan(), strVal.AsSpan()));

        Assert.Equal(
            OtlpAttributeFormatter.CalculateInt64Size(key.AsSpan(), intVal),
            OtlpAttributeFormatter.CalculateInt64Size(key.AsSpan(), intVal));

        Assert.Equal(
            OtlpAttributeFormatter.CalculateBoolSize(key.AsSpan(), boolVal),
            OtlpAttributeFormatter.CalculateBoolSize(key.AsSpan(), boolVal));

        Assert.Equal(
            OtlpAttributeFormatter.CalculateDoubleSize(key.AsSpan(), dblVal),
            OtlpAttributeFormatter.CalculateDoubleSize(key.AsSpan(), dblVal));
    }

    // ------------------------------------------------------------------
    // Edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void AllTypes_WithEmptyKey_ProduceValidPayloads()
    {
        var buf1 = CreateBuffer();
        var w1 = new ZeroAllocProtobufWriter(buf1);
        OtlpAttributeFormatter.WriteString(ref w1, ReadOnlySpan<char>.Empty, "v".AsSpan());
        Assert.True(w1.BytesWritten > 0);

        var buf2 = CreateBuffer();
        var w2 = new ZeroAllocProtobufWriter(buf2);
        OtlpAttributeFormatter.WriteInt64(ref w2, ReadOnlySpan<char>.Empty, 7L);
        Assert.True(w2.BytesWritten > 0);

        var buf3 = CreateBuffer();
        var w3 = new ZeroAllocProtobufWriter(buf3);
        OtlpAttributeFormatter.WriteBool(ref w3, ReadOnlySpan<char>.Empty, false);
        Assert.True(w3.BytesWritten > 0);

        var buf4 = CreateBuffer();
        var w4 = new ZeroAllocProtobufWriter(buf4);
        OtlpAttributeFormatter.WriteDouble(ref w4, ReadOnlySpan<char>.Empty, 0.0);
        Assert.True(w4.BytesWritten > 0);
    }

    [Fact]
    public void WriteMethods_DoNotThrow_OnLargeInputs()
    {
        string hugeKey = new string('k', 2_000);
        string hugeValue = new string('v', 10_000);

        int size = OtlpAttributeFormatter.CalculateStringSize(hugeKey.AsSpan(), hugeValue.AsSpan());

        var buffer = CreateBuffer(size + 128);
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, hugeKey.AsSpan(), hugeValue.AsSpan());

        AssertSizeMatchesWritten(size, writer);
        Assert.True(writer.BytesWritten > 12_000);
    }

    [Fact]
    public void WriteString_KeyAndValueAppearAsUtf8()
    {
        var buffer = CreateBuffer();
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, "hello".AsSpan(), "world".AsSpan());

        var written = buffer[..writer.BytesWritten];
        Assert.Contains((byte)'h', written.ToArray());
        Assert.Contains((byte)'w', written.ToArray());
    }

    // ------------------------------------------------------------------
    // VarInt size edge cases (via ProtobufMath)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(ulong.MaxValue, 10)]
    public void ProtobufMath_GetVarIntSize_IsCorrect(ulong value, int expectedSize)
    {
        Assert.Equal(expectedSize, ProtobufMath.GetVarIntSize(value));
    }
}