using System.Buffers;
using System.Text;

using KetPsi.Extensions.Logging.Abstractions;

namespace Abstractions
{
    public static class TestHelpers
    {
        public static ArrayBufferWriter<byte> CreateWriter(int capacity = 256)
            => new(capacity);

        public static string GetString(ArrayBufferWriter<byte> writer)
            => Encoding.UTF8.GetString(writer.WrittenSpan);

        public static string FormatToString<T>(T value) where T : IDeferredUtf8Formatter
        {
            var writer = CreateWriter();
            value.FormatTo(writer);
            return GetString(writer);
        }
    }
    public class MaskedStringTests
    {
        // ---------- Construction / empty / null ----------

        [Fact]
        public void Constructor_NullValue_BecomesEmpty()
        {
            var m = new MaskedString(null!, 0, 0);
            Assert.Equal(string.Empty, TestHelpers.FormatToString(m));
        }

        [Fact]
        public void FormatTo_EmptyString_WritesNothing()
        {
            var m = new MaskedString(string.Empty, 0, 5);
            var writer = TestHelpers.CreateWriter();
            m.FormatTo(writer);
            Assert.Equal(0, writer.WrittenCount);
        }

        // ---------- ApplyMask – various Index combinations ----------

        [Theory]
        [InlineData("1234567890", 0, 4, "****567890")]          // from start
        [InlineData("1234567890", 2, 6, "12****7890")]          // middle
        [InlineData("1234567890", 6, 10, "123456****")]         // to end
        [InlineData("1234567890", 0, 10, "**********")]         // full
        [InlineData("1234567890", 5, 5, "1234567890")]          // empty range
        [InlineData("1234567890", 8, 3, "1234567890")]          // start > end → no mask
        public void FormatTo_MasksCorrectly_FromStart(string input, int start, int end, string expected)
        {
            var m = new MaskedString(input, start, end);
            Assert.Equal(expected, TestHelpers.FormatToString(m));
        }

        [Theory]
        [InlineData("1234567890", 4, 0, "123456****")]          // last 4 chars (end = ^0)
        [InlineData("1234567890", 3, 3, "1234567890")]          // empty from-end range
        public void FormatTo_MasksCorrectly_FromEnd(string input, int startFromEnd, int endFromEnd, string expected)
        {
            var m = new MaskedString(input, ^startFromEnd, ^endFromEnd);
            Assert.Equal(expected, TestHelpers.FormatToString(m));
        }

        [Fact]
        public void FormatTo_MixedFromStartAndFromEnd()
        {
            // mask from index 2 to the last 3 characters
            var m = new MaskedString("1234567890", 2, ^3);
            Assert.Equal("12*****890", TestHelpers.FormatToString(m));
        }

        // ---------- TryFormat ----------

        [Fact]
        public void TryFormat_Succeeds_WhenDestinationIsLargeEnough()
        {
            var m = new MaskedString("password", 0, 4);
            Span<char> dest = stackalloc char[16];
            bool ok = m.TryFormat(dest, out int written, default, null);

            Assert.True(ok);
            Assert.Equal(8, written);
            Assert.Equal("****word", dest[..written].ToString());
        }

        [Fact]
        public void TryFormat_Fails_WhenDestinationIsTooSmall()
        {
            var m = new MaskedString("password", 0, 4);
            Span<char> dest = stackalloc char[4]; // too small
            bool ok = m.TryFormat(dest, out int written, default, null);

            Assert.False(ok);
            Assert.Equal(0, written);
        }

        // ---------- Large string (ArrayPool path) ----------

        [Fact]
        public void FormatTo_LargeString_UsesArrayPoolAndMasksCorrectly()
        {
            var large = new string('A', 1000) + "SECRET" + new string('B', 1000);
            var m = new MaskedString(large, 1000, 1006); // mask "SECRET"
            var result = TestHelpers.FormatToString(m);

            Assert.Equal(2006, result.Length);
            Assert.Equal(new string('A', 1000), result[..1000]);
            Assert.Equal("******", result[1000..1006]);
            Assert.Equal(new string('B', 1000), result[1006..]);
        }

        // ---------- ToString fallback ----------

        [Fact]
        public void ToString_ReturnsFallbackMessage()
        {
            var m = new MaskedString("secret", 0, 6);
            Assert.Equal("MaskedString fallback", m.ToString());
            Assert.Equal("MaskedString fallback", m.ToString(null, null));
        }

        // ---------- IDeferredUtf8Formatter ----------

        [Fact]
        public void Implements_IDeferredUtf8Formatter()
        {
            IDeferredUtf8Formatter f = new MaskedString("abc", 1, 2);
            Assert.Equal("a*c", TestHelpers.FormatToString(f));
        }
    }
}
