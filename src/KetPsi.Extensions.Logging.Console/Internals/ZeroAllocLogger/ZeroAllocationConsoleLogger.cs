using System.Buffers;

using KetPsi.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

internal sealed class ZeroAllocationConsoleLogger(
    string categoryName,
    ConsoleLogProcessor processor,
    IZeroAllocConsoleFormatter formatter,
    IOptionsMonitor<ConsoleLoggerOptions>? consoleOptions) : ILogger
{
    private readonly string _categoryName = categoryName;
    private readonly ConsoleLogProcessor _processor = processor;
    private readonly IZeroAllocConsoleFormatter _formatter = formatter;
    private readonly IOptionsMonitor<ConsoleLoggerOptions>? _consoleOptions = consoleOptions;
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _tsBuffer;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var buffer = _tsBuffer ??= new ArrayBufferWriter<byte>(1024);
        buffer.Clear();

        _formatter.Format(buffer, _categoryName, logLevel, eventId, state, exception, formatter);

        var errorThreshold = _consoleOptions?.CurrentValue.LogToStandardErrorThreshold ?? LogLevel.None;
        bool isErrorStream = logLevel >= errorThreshold;

        _processor.Enqueue(buffer.WrittenSpan, isErrorStream);
    }

    // Always returns true (unless None), relying on the outer Microsoft wrapper for appsettings.json rule filtering
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var node = ScopeNodePool.Rent();
        node.Parent = ScopeContext.Current;

        if (ScopeFormatterCache<TState>.FormatDeferred != null)
        {
            ScopeFormatterCache<TState>.FormatDeferred(state, node.Buffer);
        }
        else if (typeof(TState) == typeof(string))
        {
            string str = System.Runtime.CompilerServices.Unsafe.As<TState, string>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in state));
            WriteString(node.Buffer, str);
        }
        else
        {
            WriteString(node.Buffer, state.ToString() ?? string.Empty);
        }

        ScopeContext.Current = node;
        return node;
    }

    private static void WriteString(ArrayBufferWriter<byte> buffer, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(value.Length);
        int written = System.Text.Encoding.UTF8.GetBytes(value, buffer.GetSpan(maxBytes));
        buffer.Advance(written);
    }
}
internal static class ScopeFormatterCache<TState>
{
    public static readonly Action<TState, IBufferWriter<byte>>? FormatDeferred;

    static ScopeFormatterCache()
    {
        if (typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(TState)))
        {
            var method = typeof(ScopeFormatterCache<TState>)
                .GetMethod(nameof(FormatInternal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(TState));

            FormatDeferred = (Action<TState, IBufferWriter<byte>>)Delegate.CreateDelegate(typeof(Action<TState, IBufferWriter<byte>>), method);
        }
    }

    // Because this method is constrained (where TActual : IDeferredUtf8Formatter), 
    // invoking FormatTo does NOT box the struct!
    private static void FormatInternal<TActual>(TActual state, IBufferWriter<byte> writer) where TActual : IDeferredUtf8Formatter
    {
        state.FormatTo(writer);
    }
}