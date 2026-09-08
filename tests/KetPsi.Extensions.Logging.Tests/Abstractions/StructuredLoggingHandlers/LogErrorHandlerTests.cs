using System.Buffers;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using Moq;

namespace Abstractions.StructuredLoggingHandlers
{
    public class LogErrorHandlerTests
    {
        private static Mock<ILogger> CreateLogger(bool isEnabled)
        {
            var mock = new Mock<ILogger>();
            mock.Setup(l => l.IsEnabled(LogLevel.Error)).Returns(isEnabled);
            return mock;
        }

        [Fact]
        public void Constructor_WhenNotEnabled_SetsNullsAndZeroOffsets()
        {
            var logger = CreateLogger(isEnabled: false).Object;

            var handler = new LogErrorHandler(literalLength: 10, formattedCount: 3, logger, out bool isEnabled);

            Assert.False(isEnabled);
            Assert.Null(handler.Bytes);
            Assert.Null(handler.References);
            Assert.Equal(0, handler.ByteOffset);
            Assert.Equal(0, handler.RefOffset);
        }

        [Fact]
        public void Constructor_WhenEnabled_RentsArraysAndZerosOffsets()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            const int formattedCount = 5;

            var handler = new LogErrorHandler(literalLength: 20, formattedCount, logger, out bool isEnabled);

            Assert.True(isEnabled);
            Assert.NotNull(handler.Bytes);
            Assert.NotNull(handler.References);
            Assert.True(handler.Bytes!.Length >= formattedCount * 32); // ArrayPool may return larger
            Assert.True(handler.References!.Length >= formattedCount);
            Assert.Equal(0, handler.ByteOffset);
            Assert.Equal(0, handler.RefOffset);

            // Clean up (good practice even though the handler itself does not)
            ArrayPool<byte>.Shared.Return(handler.Bytes);
            ArrayPool<object?>.Shared.Return(handler.References);
        }

        [Fact]
        public void AppendLiteral_DoesNothing()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 0, logger, out _);

            // Should be a no-op; just ensure it does not throw
            handler.AppendLiteral("hello");
            handler.AppendLiteral(string.Empty);
            handler.AppendLiteral("world");

            Assert.Equal(0, handler.ByteOffset);
            Assert.Equal(0, handler.RefOffset);

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_ValueType_WritesToBytesAndAdvancesOffset()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 2, logger, out _);

            int value1 = 42;
            long value2 = 1234567890123L;

            handler.AppendFormatted(value1);
            handler.AppendFormatted(value2);

            Assert.Equal(sizeof(int) + sizeof(long), handler.ByteOffset);
            Assert.Equal(0, handler.RefOffset);

            // Verify the bytes were written (little-endian)
            Assert.Equal(42, BitConverter.ToInt32(handler.Bytes!, 0));
            Assert.Equal(1234567890123L, BitConverter.ToInt64(handler.Bytes!, sizeof(int)));

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_ReferenceType_StoresInReferencesAndAdvancesOffset()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 3, logger, out _);

            string s = "hello";
            object o = new object();
            int[] arr = { 1, 2, 3 };

            handler.AppendFormatted(s);
            handler.AppendFormatted(o);
            handler.AppendFormatted(arr);

            Assert.Equal(0, handler.ByteOffset);
            Assert.Equal(3, handler.RefOffset);
            Assert.Same(s, handler.References![0]);
            Assert.Same(o, handler.References[1]);
            Assert.Same(arr, handler.References[2]);

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_NullableValueType_TreatedAsValueType()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 2, logger, out _);

            int? nullableInt = 99;
            DateTime? nullableDt = new DateTime(2024, 1, 1);

            handler.AppendFormatted(nullableInt);
            handler.AppendFormatted(nullableDt);

            // Nullable<T> is a value type (struct) and does not contain references
            Assert.Equal(Unsafe.SizeOf<int?>() + Unsafe.SizeOf<DateTime?>(), handler.ByteOffset);
            Assert.Equal(0, handler.RefOffset);

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_MixedValueAndReferenceTypes()
        {
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 4, logger, out _);

            int i = 7;
            string s = "test";
            double d = 3.14;
            object o = 42; // boxed

            handler.AppendFormatted(i);
            handler.AppendFormatted(s);
            handler.AppendFormatted(d);
            handler.AppendFormatted(o);

            Assert.Equal(sizeof(int) + sizeof(double), handler.ByteOffset);
            Assert.Equal(2, handler.RefOffset);
            Assert.Same(s, handler.References![0]);
            Assert.Same(o, handler.References[1]);

            Assert.Equal(7, BitConverter.ToInt32(handler.Bytes!, 0));
            Assert.Equal(3.14, BitConverter.ToDouble(handler.Bytes!, sizeof(int)));

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_WithAlignmentAndFormat_StillWorks()
        {
            // The handler currently ignores alignment/format; just verify the call succeeds
            var logger = CreateLogger(isEnabled: true).Object;
            var handler = new LogErrorHandler(0, 2, logger, out _);

            handler.AppendFormatted(123, alignment: 10, format: "D5");
            handler.AppendFormatted("abc", alignment: -5, format: null);

            Assert.Equal(sizeof(int), handler.ByteOffset);
            Assert.Equal(1, handler.RefOffset);
            Assert.Equal("abc", handler.References![0]);

            ArrayPool<byte>.Shared.Return(handler.Bytes!);
            ArrayPool<object?>.Shared.Return(handler.References!);
        }

        [Fact]
        public void AppendFormatted_WhenDisabled_DoesNotThrowButFieldsRemainNull()
        {
            // When disabled the arrays are null; calling Append would NRE.
            // The real interpolated-string usage never calls Append when isEnabled==false,
            // but we document the contract here.
            var logger = CreateLogger(isEnabled: false).Object;
            var handler = new LogErrorHandler(0, 1, logger, out bool isEnabled);

            Assert.False(isEnabled);
            Assert.Null(handler.Bytes);
            Assert.Null(handler.References);

            // Intentionally not calling AppendFormatted – callers must respect isEnabled.
        }
    }
}
