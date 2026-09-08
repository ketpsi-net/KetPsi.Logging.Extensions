using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using KetPsi.Extensions.Logging.Abstractions;

using Xunit;

namespace Abstractions;

public class LogParameterTests
{
    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ArrayBufferWriter<byte> CreateWriter(int capacity = 256)
        => new(capacity);

    private static string GetString(ArrayBufferWriter<byte> writer)
        => Encoding.UTF8.GetString(writer.WrittenSpan);

    // ---------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------

    [Fact]
    public void Constructor_StoresNameValueAndFormat()
    {
        var p = new LogParameter<int>("count", 42, "D5");

        Assert.Equal("count", p.Name);
        Assert.Equal(42, p.Value);
        Assert.Equal("D5", p.Format);
    }

    [Fact]
    public void Constructor_AllowsNullNameAndNullFormat()
    {
        var p = new LogParameter<string>(null, "hello");

        Assert.Null(p.Name);
        Assert.Equal("hello", p.Value);
        Assert.Null(p.Format);
    }

    [Fact]
    public void Constructor_AllowsNullValue()
    {
        var p = new LogParameter<string?>("msg", null);

        Assert.Null(p.Value);
    }

    // ---------------------------------------------------------------------
    // ToKeyValuePair
    // ---------------------------------------------------------------------

    [Fact]
    public void ToKeyValuePair_ReturnsExpectedPair()
    {
        var p = new LogParameter<int>("id", 123);
        var kvp = p.ToKeyValuePair();

        Assert.Equal("id", kvp.Key);
        Assert.Equal(123, kvp.Value);
    }

    [Fact]
    public void ToKeyValuePair_NullName_ProducesNullKey()
    {
        var p = new LogParameter<string>(null, "value");
        var kvp = p.ToKeyValuePair();

        Assert.Null(kvp.Key);
        Assert.Equal("value", kvp.Value);
    }

    [Fact]
    public void ToKeyValuePair_NullValue_StoresNull()
    {
        var p = new LogParameter<object?>("obj", null);
        var kvp = p.ToKeyValuePair();

        Assert.Null(kvp.Value);
    }

    // ---------------------------------------------------------------------
    // FormatTo – types that implement IUtf8SpanFormattable
    // (int, long, Guid, DateTime, DateTimeOffset, etc. in modern .NET)
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_Int_UsesUtf8Formatter()
    {
        var p = new LogParameter<int>("n", 42);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("42", GetString(writer));
    }

    [Fact]
    public void FormatTo_Int_WithFormatString()
    {
        var p = new LogParameter<int>("n", 255, "X4");
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("00FF", GetString(writer));
    }

    [Fact]
    public void FormatTo_Guid_UsesUtf8Formatter()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var p = new LogParameter<Guid>("id", guid);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal(guid.ToString(), GetString(writer));
    }

    [Fact]
    public void FormatTo_DateTime_WithRoundTripFormat()
    {
        var dt = new DateTime(2024, 6, 15, 13, 45, 30, DateTimeKind.Utc);
        var p = new LogParameter<DateTime>("ts", dt, "O");
        var writer = CreateWriter();

        p.FormatTo(writer);

        // "O" format is culture-invariant
        Assert.Equal(dt.ToString("O", CultureInfo.InvariantCulture), GetString(writer));
    }

    // ---------------------------------------------------------------------
    // FormatTo – types that implement ISpanFormattable but NOT IUtf8SpanFormattable
    // (rare in BCL, so we use a custom test type)
    // ---------------------------------------------------------------------

    private readonly struct CharOnlyFormattable : ISpanFormattable
    {
        public readonly int Value;
        public CharOnlyFormattable(int value) => Value = value;

        public bool TryFormat(Span<char> destination, out int charsWritten,
                              ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var s = $"CHAR:{Value}";
            if (s.Length > destination.Length)
            {
                charsWritten = 0;
                return false;
            }
            s.AsSpan().CopyTo(destination);
            charsWritten = s.Length;
            return true;
        }

        public string ToString(string? format, IFormatProvider? formatProvider)
            => $"CHAR:{Value}";
    }

    [Fact]
    public void FormatTo_CustomISpanFormattable_UsesCharFormatterPath()
    {
        var p = new LogParameter<CharOnlyFormattable>("c", new CharOnlyFormattable(99));
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("CHAR:99", GetString(writer));
    }

    // ---------------------------------------------------------------------
    // FormatTo – types that implement neither interface (ToString fallback)
    // ---------------------------------------------------------------------

    private class NoFormatInterfaces
    {
        public override string ToString() => "FALLBACK";
    }

    [Fact]
    public void FormatTo_TypeWithoutAnyFormatInterface_UsesToString()
    {
        var p = new LogParameter<NoFormatInterfaces>("x", new NoFormatInterfaces());
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("FALLBACK", GetString(writer));
    }

    [Fact]
    public void FormatTo_AnonymousType_UsesToString()
    {
        var p = new LogParameter<object>("o", new { A = 1, B = "two" });
        var writer = CreateWriter();

        p.FormatTo(writer);

        var result = GetString(writer);
        Assert.Contains("A = 1", result);
        Assert.Contains("B = two", result);
    }

    // ---------------------------------------------------------------------
    // Null / empty / whitespace values
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_NullReferenceType_WritesEmpty()
    {
        var p = new LogParameter<string?>("msg", null);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal(0, writer.WrittenCount);
        Assert.Equal(string.Empty, GetString(writer));
    }

    [Fact]
    public void FormatTo_EmptyString_WritesNothing()
    {
        var p = new LogParameter<string>("msg", string.Empty);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void FormatTo_WhitespaceString_IsWritten()
    {
        var p = new LogParameter<string>("msg", "   \t");
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("   \t", GetString(writer));
    }

    [Fact]
    public void FormatTo_NullableValueType_WithValue()
    {
        int? value = 42;
        var p = new LogParameter<int?>("n", value);
        var writer = CreateWriter();

        p.FormatTo(writer);

        // int? does not implement IUtf8SpanFormattable / ISpanFormattable,
        // so it goes through ToString()
        Assert.Equal("42", GetString(writer));
    }

    [Fact]
    public void FormatTo_NullableValueType_Null_WritesEmpty()
    {
        var p = new LogParameter<int?>("n", null);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal(0, writer.WrittenCount);
    }

    // ---------------------------------------------------------------------
    // Large values (exercise buffer growth)
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_LargeString_WritesCorrectly()
    {
        var large = new string('A', 10_000);
        var p = new LogParameter<string>("big", large);
        var writer = CreateWriter(capacity: 64); // deliberately small

        p.FormatTo(writer);

        Assert.Equal(large, GetString(writer));
    }

    [Fact]
    public void FormatTo_LargeFormattedNumber_StillWorks()
    {
        // int with a format that produces a longer string is still tiny,
        // but we keep the test for completeness
        var p = new LogParameter<long>("n", long.MaxValue, "N0");
        var writer = CreateWriter();

        p.FormatTo(writer);

        var expected = long.MaxValue.ToString("N0", CultureInfo.InvariantCulture);
        // Note: the Utf8 formatter uses the current culture of the provider
        // (null → CultureInfo.CurrentCulture). For determinism we accept either.
        Assert.True(
            GetString(writer) == long.MaxValue.ToString("N0") ||
            GetString(writer) == expected);
    }

    // ---------------------------------------------------------------------
    // Format string is forwarded (or ignored when null)
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_NullFormat_StillFormats()
    {
        var p = new LogParameter<int>("n", 42, format: null);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("42", GetString(writer));
    }

    [Fact]
    public void FormatTo_EmptyFormat_StillFormats()
    {
        var p = new LogParameter<int>("n", 42, format: "");
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("42", GetString(writer));
    }

    // ---------------------------------------------------------------------
    // Multiple calls are independent
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_CanBeCalledMultipleTimes()
    {
        var p = new LogParameter<int>("n", 123);

        var w1 = CreateWriter();
        var w2 = CreateWriter();

        p.FormatTo(w1);
        p.FormatTo(w2);

        Assert.Equal("123", GetString(w1));
        Assert.Equal("123", GetString(w2));
    }

    // ---------------------------------------------------------------------
    // IDeferredUtf8Formatter contract
    // ---------------------------------------------------------------------

    [Fact]
    public void LogParameter_Implements_IDeferredUtf8Formatter()
    {
        IDeferredUtf8Formatter formatter = new LogParameter<string>("k", "v");
        var writer = CreateWriter();

        formatter.FormatTo(writer);

        Assert.Equal("v", GetString(writer));
    }

    // ---------------------------------------------------------------------
    // Enum (neither interface in older runtimes, ToString fallback)
    // ---------------------------------------------------------------------

    [Fact]
    public void FormatTo_Enum_UsesToString()
    {
        var p = new LogParameter<DayOfWeek>("day", DayOfWeek.Friday);
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("Friday", GetString(writer));
    }

    // ---------------------------------------------------------------------
    // Custom IUtf8SpanFormattable (highest priority path)
    // ---------------------------------------------------------------------

    private readonly struct Utf8Formattable : IUtf8SpanFormattable
    {
        public readonly int Value;
        public Utf8Formattable(int value) => Value = value;

        public bool TryFormat(Span<byte> destination, out int bytesWritten,
                              ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var s = $"UTF8:{Value}";
            if (Encoding.UTF8.GetByteCount(s) > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }
            bytesWritten = Encoding.UTF8.GetBytes(s, destination);
            return true;
        }
    }

    [Fact]
    public void FormatTo_CustomIUtf8SpanFormattable_UsesUtf8Path()
    {
        var p = new LogParameter<Utf8Formattable>("u", new Utf8Formattable(77));
        var writer = CreateWriter();

        p.FormatTo(writer);

        Assert.Equal("UTF8:77", GetString(writer));
    }
}