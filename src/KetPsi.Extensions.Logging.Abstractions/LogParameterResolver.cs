namespace KetPsi.Extensions.Logging.Abstractions;

internal static class LogParameterResolver<T>
{
    public delegate bool Utf8Delegate(T value, Span<byte> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov);
    public delegate bool CharDelegate(T value, Span<char> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov);

    public static readonly Utf8Delegate? Utf8Formatter;
    public static readonly CharDelegate? CharFormatter;

    static LogParameterResolver()
    {
        if (typeof(IUtf8SpanFormattable).IsAssignableFrom(typeof(T)))
        {
            var method = typeof(LogParameterResolver<T>)
                .GetMethod(nameof(FormatUtf8), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(T));
            Utf8Formatter = (Utf8Delegate)Delegate.CreateDelegate(typeof(Utf8Delegate), method);
        }
        else if (typeof(ISpanFormattable).IsAssignableFrom(typeof(T)))
        {
            var method = typeof(LogParameterResolver<T>)
                .GetMethod(nameof(FormatChar), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(T));
            CharFormatter = (CharDelegate)Delegate.CreateDelegate(typeof(CharDelegate), method);
        }
    }

    private static bool FormatUtf8<TActual>(TActual value, Span<byte> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov) where TActual : IUtf8SpanFormattable
    {
        return value.TryFormat(dest, out written, fmt, prov);
    }

    private static bool FormatChar<TActual>(TActual value, Span<char> dest, out int written, ReadOnlySpan<char> fmt, IFormatProvider? prov) where TActual : ISpanFormattable
    {
        return value.TryFormat(dest, out written, fmt, prov);
    }
}
