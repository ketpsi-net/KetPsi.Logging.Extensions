using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace KetPsi.Extensions.Logging.Abstractions;

/// <summary>
/// Boxing-free, NativeAOT-safe formatting strategy for values of type <typeparamref name="T"/>.
/// </summary>
/// <remarks>
/// Instances are created through the constrained <c>Register*</c> methods on <see cref="LogValueFormatter"/>,
/// which the source generator calls from a module initializer for every value type that appears in a log call.
/// Because every instantiation is visible at compile time, no reflection (<c>MakeGenericMethod</c>) is needed.
/// </remarks>
public abstract class LogValueFormatter<T>
{
    /// <summary>The registered formatter for <typeparamref name="T"/>, or <see langword="null"/> when none is known.</summary>
    public static LogValueFormatter<T>? Default { get; internal set; } = LogValueFormatter.CreateBuiltIn<T>();

    /// <summary>Writes <paramref name="value"/> as UTF-8 into <paramref name="writer"/>.</summary>
    public abstract void FormatTo(in T value, IBufferWriter<byte> writer, string? format);

    /// <summary>Formats <paramref name="value"/> as UTF-16. Returns <see langword="false"/> when <paramref name="destination"/> is too small.</summary>
    public abstract bool TryFormat(in T value, Span<char> destination, out int charsWritten, string? format);

    /// <summary>Formats <paramref name="value"/> as UTF-8. Returns <see langword="false"/> when <paramref name="utf8Destination"/> is too small.</summary>
    public virtual bool TryFormatUtf8(in T value, Span<byte> utf8Destination, out int bytesWritten, string? format)
        => LogValueFormatter.TryFormatUtf8ViaChars(this, in value, utf8Destination, out bytesWritten, format);
}

/// <summary>
/// Registration and formatting entry points for <see cref="LogValueFormatter{T}"/>.
/// </summary>
public static class LogValueFormatter
{
    // Upper bound for growing a destination buffer before giving up on span formatting.
    private const int MaxFormatBufferSize = 1 << 20;
    private const int StackCharLimit = 256;

    // Registration is for value types only: reference types are formatted through interface checks,
    // which do not allocate for them. The struct constraint also keeps null checks (which box) out of the hot path.
    public static void RegisterFormattable<T>() where T : struct, IUtf8SpanFormattable, ISpanFormattable
        => LogValueFormatter<T>.Default ??= new FormattableFormatter<T>();

    public static void RegisterSpanFormattable<T>() where T : struct, ISpanFormattable
        => LogValueFormatter<T>.Default ??= new SpanFormattableFormatter<T>();

    public static void RegisterUtf8Formattable<T>() where T : struct, IUtf8SpanFormattable
        => LogValueFormatter<T>.Default ??= new Utf8FormattableFormatter<T>();

    public static void RegisterDeferred<T>() where T : struct, IDeferredUtf8Formatter, ISpanFormattable
        => LogValueFormatter<T>.Default ??= new DeferredFormatter<T>();

    public static void RegisterNullable<T>() where T : struct
        => LogValueFormatter<T?>.Default ??= new NullableFormatter<T>();

    /// <summary>Writes <paramref name="value"/> as UTF-8 using the registered formatter, falling back to interface checks and <see cref="object.ToString"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FormatTo<T>(in T value, IBufferWriter<byte> writer, string? format)
    {
        if (typeof(T) == typeof(string))
        {
            WriteString(writer, Unsafe.As<T, string?>(ref Unsafe.AsRef(in value)));
            return;
        }

        var formatter = LogValueFormatter<T>.Default;
        if (formatter != null)
        {
            formatter.FormatTo(in value, writer, format);
            return;
        }

        FormatFallback(in value, writer, format);
    }

    /// <summary>Formats <paramref name="value"/> as UTF-16. Returns <see langword="false"/> when <paramref name="destination"/> is too small.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFormat<T>(in T value, Span<char> destination, out int charsWritten, string? format)
    {
        if (typeof(T) == typeof(string))
            return TryCopy(Unsafe.As<T, string?>(ref Unsafe.AsRef(in value)), destination, out charsWritten);

        var formatter = LogValueFormatter<T>.Default;
        if (formatter != null)
            return formatter.TryFormat(in value, destination, out charsWritten, format);

        return TryFormatFallback(in value, destination, out charsWritten, format);
    }

    /// <summary>Formats <paramref name="value"/> as UTF-8. Returns <see langword="false"/> when <paramref name="utf8Destination"/> is too small.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFormatUtf8<T>(in T value, Span<byte> utf8Destination, out int bytesWritten, string? format)
    {
        if (typeof(T) == typeof(string))
        {
            string? s = Unsafe.As<T, string?>(ref Unsafe.AsRef(in value));
            return Utf8.FromUtf16(s.AsSpan(), utf8Destination, out _, out bytesWritten) == OperationStatus.Done;
        }

        var formatter = LogValueFormatter<T>.Default;
        if (formatter != null)
            return formatter.TryFormatUtf8(in value, utf8Destination, out bytesWritten, format);

        if (!typeof(T).IsValueType && value is IUtf8SpanFormattable utf8)
            return utf8.TryFormat(utf8Destination, out bytesWritten, format, null);

        return TryFormatUtf8ViaChars(null, in value, utf8Destination, out bytesWritten, format);
    }

    internal static bool TryFormatUtf8ViaChars<T>(LogValueFormatter<T>? formatter, in T value, Span<byte> utf8Destination, out int bytesWritten, string? format)
    {
        bytesWritten = 0;
        // A UTF-8 destination of N bytes can never need more than N UTF-16 chars.
        int charCapacity = Math.Min(utf8Destination.Length, MaxFormatBufferSize);
        char[]? rented = null;
        Span<char> chars = charCapacity <= StackCharLimit ? stackalloc char[StackCharLimit] : (rented = ArrayPool<char>.Shared.Rent(charCapacity));
        try
        {
            int charsWritten;
            bool ok = formatter != null
                ? formatter.TryFormat(in value, chars, out charsWritten, format)
                : TryFormat(in value, chars, out charsWritten, format);
            if (!ok) return false;
            return Utf8.FromUtf16(chars[..charsWritten], utf8Destination, out _, out bytesWritten) == OperationStatus.Done;
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    // Unregistered types. Reference types hit the interface checks without allocating;
    // unregistered value types box here, which is why the generator registers every value type it sees.
    private static void FormatFallback<T>(in T value, IBufferWriter<byte> writer, string? format)
    {
        if (value is null) return;
        if (typeof(T) == typeof(bool))
        {
            writer.Write(Unsafe.As<T, bool>(ref Unsafe.AsRef(in value)) ? "True"u8 : "False"u8);
            return;
        }

        object boxed = value;
        switch (boxed)
        {
            case IDeferredUtf8Formatter deferred:
                deferred.FormatTo(writer);
                return;
            case IUtf8SpanFormattable utf8:
                if (TryWriteUtf8(utf8, writer, format)) return;
                break;
            case ISpanFormattable span:
                if (TryWriteSpan(span, writer, format)) return;
                break;
        }

        WriteString(writer, boxed.ToString());
    }

    private static bool TryFormatFallback<T>(in T value, Span<char> destination, out int charsWritten, string? format)
    {
        if (value is null)
        {
            charsWritten = 0;
            return true;
        }
        if (typeof(T) == typeof(bool))
            return Unsafe.As<T, bool>(ref Unsafe.AsRef(in value)).TryFormat(destination, out charsWritten);

        object boxed = value;
        if (boxed is ISpanFormattable span)
            return span.TryFormat(destination, out charsWritten, format, null);

        return TryCopy(boxed.ToString(), destination, out charsWritten);
    }

    private static bool TryWriteUtf8<TValue>(TValue value, IBufferWriter<byte> writer, string? format) where TValue : IUtf8SpanFormattable
    {
        int sizeHint = 64;
        while (true)
        {
            Span<byte> destination = writer.GetSpan(sizeHint);
            if (value.TryFormat(destination, out int written, format, null))
            {
                writer.Advance(written);
                return true;
            }
            if (destination.Length >= MaxFormatBufferSize) return false;
            sizeHint = destination.Length * 2;
        }
    }

    private static bool TryWriteSpan<TValue>(TValue value, IBufferWriter<byte> writer, string? format) where TValue : ISpanFormattable
    {
        Span<char> stackBuffer = stackalloc char[StackCharLimit];
        if (value.TryFormat(stackBuffer, out int charsWritten, format, null))
        {
            WriteChars(writer, stackBuffer[..charsWritten]);
            return true;
        }

        int size = StackCharLimit * 4;
        while (size <= MaxFormatBufferSize)
        {
            char[] rented = ArrayPool<char>.Shared.Rent(size);
            try
            {
                if (value.TryFormat(rented, out charsWritten, format, null))
                {
                    WriteChars(writer, rented.AsSpan(0, charsWritten));
                    return true;
                }
                size = rented.Length * 2;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
        return false;
    }

    internal static void WriteChars(IBufferWriter<byte> writer, ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        int written = Encoding.UTF8.GetBytes(chars, writer.GetSpan(Encoding.UTF8.GetMaxByteCount(chars.Length)));
        writer.Advance(written);
    }

    internal static void WriteString(IBufferWriter<byte> writer, string? value)
    {
        if (!string.IsNullOrEmpty(value)) WriteChars(writer, value.AsSpan());
    }

    private static bool TryCopy(string? value, Span<char> destination, out int charsWritten)
    {
        value ??= string.Empty;
        if (value.Length > destination.Length)
        {
            charsWritten = 0;
            return false;
        }
        value.AsSpan().CopyTo(destination);
        charsWritten = value.Length;
        return true;
    }

    // Common BCL types are pre-registered so hand-built LogParameter<T> values stay allocation-free
    // even without the generator. typeof checks are folded by the JIT and are AOT-safe.
    internal static LogValueFormatter<T>? CreateBuiltIn<T>()
    {
        if (typeof(T) == typeof(int)) return (LogValueFormatter<T>)(object)new FormattableFormatter<int>();
        if (typeof(T) == typeof(long)) return (LogValueFormatter<T>)(object)new FormattableFormatter<long>();
        if (typeof(T) == typeof(short)) return (LogValueFormatter<T>)(object)new FormattableFormatter<short>();
        if (typeof(T) == typeof(byte)) return (LogValueFormatter<T>)(object)new FormattableFormatter<byte>();
        if (typeof(T) == typeof(sbyte)) return (LogValueFormatter<T>)(object)new FormattableFormatter<sbyte>();
        if (typeof(T) == typeof(ushort)) return (LogValueFormatter<T>)(object)new FormattableFormatter<ushort>();
        if (typeof(T) == typeof(uint)) return (LogValueFormatter<T>)(object)new FormattableFormatter<uint>();
        if (typeof(T) == typeof(ulong)) return (LogValueFormatter<T>)(object)new FormattableFormatter<ulong>();
        if (typeof(T) == typeof(float)) return (LogValueFormatter<T>)(object)new FormattableFormatter<float>();
        if (typeof(T) == typeof(double)) return (LogValueFormatter<T>)(object)new FormattableFormatter<double>();
        if (typeof(T) == typeof(decimal)) return (LogValueFormatter<T>)(object)new FormattableFormatter<decimal>();
        if (typeof(T) == typeof(Guid)) return (LogValueFormatter<T>)(object)new FormattableFormatter<Guid>();
        if (typeof(T) == typeof(DateTime)) return (LogValueFormatter<T>)(object)new FormattableFormatter<DateTime>();
        if (typeof(T) == typeof(DateTimeOffset)) return (LogValueFormatter<T>)(object)new FormattableFormatter<DateTimeOffset>();
        if (typeof(T) == typeof(TimeSpan)) return (LogValueFormatter<T>)(object)new FormattableFormatter<TimeSpan>();
        if (typeof(T) == typeof(DateOnly)) return (LogValueFormatter<T>)(object)new FormattableFormatter<DateOnly>();
        if (typeof(T) == typeof(TimeOnly)) return (LogValueFormatter<T>)(object)new FormattableFormatter<TimeOnly>();
        return null;
    }

    private sealed class FormattableFormatter<T> : LogValueFormatter<T> where T : struct, IUtf8SpanFormattable, ISpanFormattable
    {
        public override void FormatTo(in T value, IBufferWriter<byte> writer, string? format)
        {
            if (!TryWriteUtf8(value, writer, format)) WriteString(writer, value.ToString());
        }

        public override bool TryFormat(in T value, Span<char> destination, out int charsWritten, string? format)
            => value.TryFormat(destination, out charsWritten, format, null);

        public override bool TryFormatUtf8(in T value, Span<byte> utf8Destination, out int bytesWritten, string? format)
            => value.TryFormat(utf8Destination, out bytesWritten, format, null);
    }

    private sealed class SpanFormattableFormatter<T> : LogValueFormatter<T> where T : struct, ISpanFormattable
    {
        public override void FormatTo(in T value, IBufferWriter<byte> writer, string? format)
        {
            if (!TryWriteSpan(value, writer, format)) WriteString(writer, value.ToString());
        }

        public override bool TryFormat(in T value, Span<char> destination, out int charsWritten, string? format)
            => value.TryFormat(destination, out charsWritten, format, null);
    }

    private sealed class Utf8FormattableFormatter<T> : LogValueFormatter<T> where T : struct, IUtf8SpanFormattable
    {
        public override bool TryFormatUtf8(in T value, Span<byte> utf8Destination, out int bytesWritten, string? format)
            => value.TryFormat(utf8Destination, out bytesWritten, format, null);

        public override void FormatTo(in T value, IBufferWriter<byte> writer, string? format)
        {
            if (!TryWriteUtf8(value, writer, format)) WriteString(writer, value.ToString());
        }

        public override bool TryFormat(in T value, Span<char> destination, out int charsWritten, string? format)
        {
            charsWritten = 0;

            // Worst case one UTF-8 byte per char, so the bytes never need more room than chars * 3.
            int byteCapacity = Math.Min(destination.Length * 3, MaxFormatBufferSize);
            byte[]? rented = null;
            Span<byte> utf8 = byteCapacity <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(byteCapacity));
            try
            {
                if (!value.TryFormat(utf8, out int bytesWritten, format, null)) return false;
                return Utf8.ToUtf16(utf8[..bytesWritten], destination, out _, out charsWritten) == OperationStatus.Done;
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private sealed class DeferredFormatter<T> : LogValueFormatter<T> where T : struct, IDeferredUtf8Formatter, ISpanFormattable
    {
        public override void FormatTo(in T value, IBufferWriter<byte> writer, string? format)
            => value.FormatTo(writer);

        public override bool TryFormat(in T value, Span<char> destination, out int charsWritten, string? format)
            => value.TryFormat(destination, out charsWritten, format, null);
    }

    private sealed class NullableFormatter<T> : LogValueFormatter<T?> where T : struct
    {
        public override void FormatTo(in T? value, IBufferWriter<byte> writer, string? format)
        {
            if (!value.HasValue) return;
            T inner = value.GetValueOrDefault();
            LogValueFormatter.FormatTo(in inner, writer, format);
        }

        public override bool TryFormat(in T? value, Span<char> destination, out int charsWritten, string? format)
        {
            if (!value.HasValue)
            {
                charsWritten = 0;
                return true;
            }
            T inner = value.GetValueOrDefault();
            return LogValueFormatter.TryFormat(in inner, destination, out charsWritten, format);
        }
    }
}
