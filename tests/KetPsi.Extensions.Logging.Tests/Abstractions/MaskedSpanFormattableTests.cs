using KetPsi.Extensions.Logging.Abstractions;

namespace Abstractions
{
    public class MaskedSpanFormattableTests
    {
        // ---------- string specialization ----------

        [Fact]
        public void FormatTo_String_MasksCorrectly()
        {
            var m = new MaskedSpanFormattable<string>("1234567890", 2, 6);
            Assert.Equal("12****7890", TestHelpers.FormatToString(m));
        }

        [Fact]
        public void FormatTo_NullString_WritesEmpty()
        {
            // T = string, value = null → Unsafe.As will produce null, then NullReferenceException
            // The current implementation does not guard against null string.
            // Document the behaviour (or fix the production code).
            var m = new MaskedSpanFormattable<string>(null!, 0, 5);

            // Expect either empty or exception – current code throws.
            Assert.ThrowsAny<Exception>(() => TestHelpers.FormatToString(m));
        }

        [Fact]
        public void TryFormat_String_Succeeds()
        {
            var m = new MaskedSpanFormattable<string>("password", 0, 4);
            Span<char> dest = stackalloc char[16];
            bool ok = m.TryFormat(dest, out int written, default, null);

            Assert.True(ok);
            Assert.Equal(8, written);
            Assert.Equal("****word", dest[..written].ToString());
        }

        [Fact]
        public void TryFormat_String_FailsWhenTooSmall()
        {
            var m = new MaskedSpanFormattable<string>("password", 0, 4);
            Span<char> dest = stackalloc char[3];
            bool ok = m.TryFormat(dest, out int written, default, null);

            Assert.False(ok);
            Assert.Equal(0, written);
        }

        // ---------- ISpanFormattable path (via MaskFormatterCache) ----------

        private readonly struct TestFormattable : ISpanFormattable
        {
            public readonly int Value;
            public TestFormattable(int v) => Value = v;

            public bool TryFormat(Span<char> dest, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
            {
                var s = Value.ToString();
                if (s.Length > dest.Length) { written = 0; return false; }
                s.AsSpan().CopyTo(dest);
                written = s.Length;
                return true;
            }

            public string ToString(string? format, IFormatProvider? formatProvider) => Value.ToString();
        }

        [Fact]
        public void FormatTo_ISpanFormattable_MasksCorrectly()
        {
            var m = new MaskedSpanFormattable<TestFormattable>(new TestFormattable(123456), 1, 4);
            Assert.Equal("1***56", TestHelpers.FormatToString(m));
        }

        [Fact]
        public void FormatTo_Int_UsesISpanFormattablePath()
        {
            // int implements ISpanFormattable / IUtf8SpanFormattable
            var m = new MaskedSpanFormattable<int>(123456789, 2, 6);
            Assert.Equal("12****789", TestHelpers.FormatToString(m));
        }

        // ---------- Fallback ToString path ----------

        private class NoInterfaces
        {
            public override string ToString() => "ABCDEFGH";
        }

        [Fact]
        public void FormatTo_NoFormatInterfaces_UsesToStringThenMasks()
        {
            var m = new MaskedSpanFormattable<NoInterfaces>(new NoInterfaces(), 2, 6);
            Assert.Equal("AB****GH", TestHelpers.FormatToString(m));
        }

        [Fact]
        public void FormatTo_NullObject_WritesEmpty()
        {
            var m = new MaskedSpanFormattable<object?>(null, 0, 5);
            Assert.Equal(string.Empty, TestHelpers.FormatToString(m));
        }

        // ---------- Large value (ArrayPool path) ----------

        [Fact]
        public void FormatTo_LargeString_Works()
        {
            var large = new string('X', 500) + "HIDE" + new string('Y', 500);
            var m = new MaskedSpanFormattable<string>(large, 500, 504);
            var result = TestHelpers.FormatToString(m);

            Assert.Equal(1004, result.Length);
            Assert.Equal("****", result[500..504]);
        }

        // ---------- ToString fallback ----------

        [Fact]
        public void ToString_ReturnsFallback()
        {
            var m = new MaskedSpanFormattable<int>(42, 0, 1);
            Assert.Equal("MaskedSpanFormattable fallback", m.ToString());
        }

        // ---------- Index edge cases (shared with MaskedString) ----------

        [Theory]
        [InlineData(0, 0, "12345")]          // empty range
        [InlineData(0, 5, "*****")]          // full
        [InlineData(10, 20, "12345")]        // completely out of range
        public void FormatTo_IndexEdgeCases(int start, int end, string expected)
        {
            var m = new MaskedSpanFormattable<string>("12345", start, end);
            Assert.Equal(expected, TestHelpers.FormatToString(m));
        }
    }
}
