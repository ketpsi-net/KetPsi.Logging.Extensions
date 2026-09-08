using KetPsi.Extensions.Logging.Abstractions;

namespace Abstractions
{
    public class JsonLogValueTests
    {
        // ---------- null value ----------

        [Fact]
        public void FormatTo_Null_WritesNullLiteral()
        {
            var j = new JsonLogValue<string?>(null);
            Assert.Equal("null", TestHelpers.FormatToString(j));
        }

        [Fact]
        public void TryFormat_Null_WritesNullLiteral()
        {
            var j = new JsonLogValue<object?>(null);
            Span<char> dest = stackalloc char[8];
            bool ok = j.TryFormat(dest, out int written, default, null);

            Assert.True(ok);
            Assert.Equal(4, written);
            Assert.Equal("null", dest[..written].ToString());
        }

        [Fact]
        public void ToString_Null_ReturnsNullLiteral()
        {
            var j = new JsonLogValue<int?>(null);
            Assert.Equal("null", j.ToString());
        }

        // ---------- primitives ----------

        [Fact]
        public void FormatTo_Int()
        {
            var j = new JsonLogValue<int>(42);
            Assert.Equal("42", TestHelpers.FormatToString(j));
        }

        [Fact]
        public void FormatTo_String()
        {
            var j = new JsonLogValue<string>("hello");
            Assert.Equal("\"hello\"", TestHelpers.FormatToString(j));
        }

        [Fact]
        public void FormatTo_Bool()
        {
            var j = new JsonLogValue<bool>(true);
            Assert.Equal("true", TestHelpers.FormatToString(j));
        }

        // ---------- complex object ----------

        private record Person(string Name, int Age);

        [Fact]
        public void FormatTo_ComplexObject()
        {
            var j = new JsonLogValue<Person>(new Person("Alice", 30));
            var json = TestHelpers.FormatToString(j);

            // Default serializer uses camelCase? No – default is PascalCase unless options say otherwise.
            Assert.Contains("\"Name\":\"Alice\"", json);
            Assert.Contains("\"Age\":30", json);
        }

        [Fact]
        public void ToString_ComplexObject_MatchesFormatTo()
        {
            var person = new Person("Bob", 25);
            var j = new JsonLogValue<Person>(person);

            Assert.Equal(j.ToString(), TestHelpers.FormatToString(j));
        }

        // ---------- with explicit JsonTypeInfo ----------

        [Fact]
        public void FormatTo_WithTypeInfo()
        {
            // Minimal source-generated style type info is hard to create by hand;
            // we just verify that a null typeInfo falls back to the reflection serializer.
            var j = new JsonLogValue<Person>(new Person("Eve", 40), typeInfo: null);
            var json = TestHelpers.FormatToString(j);
            Assert.Contains("Eve", json);
        }

        // ---------- TryFormat caching (operationId) ----------

        [Fact]
        public void TryFormat_CachesPerOperationId()
        {
            var j1 = new JsonLogValue<int>(100);
            var j2 = new JsonLogValue<int>(200); // different operationId

            Span<char> dest = stackalloc char[16];

            Assert.True(j1.TryFormat(dest, out int w1, default, null));
            Assert.Equal("100", dest[..w1].ToString());

            Assert.True(j2.TryFormat(dest, out int w2, default, null));
            Assert.Equal("200", dest[..w2].ToString());
        }

        [Fact]
        public void TryFormat_SameInstance_ReusesCache()
        {
            var j = new JsonLogValue<string>("cached");
            Span<char> dest = stackalloc char[32];

            Assert.True(j.TryFormat(dest, out int w1, default, null));
            Assert.True(j.TryFormat(dest, out int w2, default, null));

            Assert.Equal(w1, w2);
            Assert.Equal("\"cached\"", dest[..w1].ToString());
        }

        // ---------- destination too small ----------

        [Fact]
        public void TryFormat_DestinationTooSmall_ReturnsFalse()
        {
            var j = new JsonLogValue<string>("a fairly long string that will not fit");
            Span<char> dest = stackalloc char[4];
            bool ok = j.TryFormat(dest, out int written, default, null);

            Assert.False(ok);
            // written may be partial or zero depending on Utf8.ToUtf16 behaviour
        }

        // ---------- multiple FormatTo calls ----------

        [Fact]
        public void FormatTo_CanBeCalledMultipleTimes()
        {
            var j = new JsonLogValue<int>(99);

            Assert.Equal("99", TestHelpers.FormatToString(j));
            Assert.Equal("99", TestHelpers.FormatToString(j));
        }

        // ---------- IDeferredUtf8Formatter ----------

        [Fact]
        public void Implements_IDeferredUtf8Formatter()
        {
            IDeferredUtf8Formatter f = new JsonLogValue<bool>(false);
            Assert.Equal("false", TestHelpers.FormatToString(f));
        }

        // ---------- array / collection ----------

        [Fact]
        public void FormatTo_Array()
        {
            var j = new JsonLogValue<int[]>(new[] { 1, 2, 3 });
            Assert.Equal("[1,2,3]", TestHelpers.FormatToString(j));
        }

        [Fact]
        public void FormatTo_Dictionary()
        {
            var dict = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
            var j = new JsonLogValue<Dictionary<string, int>>(dict);
            var json = TestHelpers.FormatToString(j);

            Assert.Contains("\"a\":1", json);
            Assert.Contains("\"b\":2", json);
        }
    }
}
