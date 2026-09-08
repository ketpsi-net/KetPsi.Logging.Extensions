using System.Buffers;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.Console.Internals.Formatters;

internal sealed class SimpleConsoleFormatter : IZeroAllocConsoleFormatter, IDisposable
{
    public SimpleConsoleFormatter(IOptionsMonitor<ZeroAllocConsoleFormatterOptions> optionsMonitor)
    {
        _options = optionsMonitor.CurrentValue;
        _optionsReloadToken = optionsMonitor.OnChange(newOptions => _options = newOptions);
    }
    private readonly IDisposable? _optionsReloadToken;
    private ZeroAllocConsoleFormatterOptions? _options;
    private readonly bool _isOutputRedirected = System.Console.IsOutputRedirected;

    public void Format<TState>(
        IBufferWriter<byte> buffer, string categoryName, LogLevel logLevel, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        bool useColors = _options is not null && (_options.ColorBehavior == LoggerColorBehavior.Enabled ||
                        (_options.ColorBehavior == LoggerColorBehavior.Default) && !_isOutputRedirected);

        // 1. Timestamp
        WriteTimestamp(buffer, _options, useColors);

        // 2. Log Level (e.g., "info: " takes up 6 characters)
        WriteLogLevel(buffer, logLevel, useColors);

        // 3. EventId (Optional)
        if (_options?.IncludeEventId == true && eventId.Id != 0)
        {
            buffer.Write("["u8);
            WriteString(buffer, eventId.Id.ToString());
            buffer.Write("] "u8);
        }

        // 4. Category & Line Break
        if (_options?.IncludeCategory == true)
        {
            if (useColors) buffer.Write("\x1b[36m"u8);
            WriteString(buffer, categoryName);
            if (useColors) buffer.Write("\x1b[0m"u8);

            // Only break to a new line if we actually printed a category
            if (!_options?.SingleLine ?? false)
            {
                buffer.Write("\n"u8);
            }
            else
            {
                buffer.Write(" "u8);
            }
        }

        // 5. Scopes (If enabled)
        if (_options?.IncludeScopes == true && ScopeContext.Current != null)
        {
            // Indent scopes only if we are on a new line (which requires a category)
            if (!(_options?.SingleLine ?? false) && _options?.IncludeCategory == true)
            {
                buffer.Write("      "u8);
            }

            WriteScopes(buffer, _options);

            if (!(_options?.SingleLine ?? false) && _options?.IncludeCategory == true)
            {
                buffer.Write("\n"u8);
            }
            else
            {
                buffer.Write(" "u8);
            }
        }

        // 6. Message Indentation
        // Indent the message only if it was pushed to a new line by the category
        if (!(_options?.SingleLine ?? false) && _options?.IncludeCategory == true)
        {
            buffer.Write("      "u8);
        }

        // 7. The actual Message
        if (LogStateFormatterCache<TState>.Format != null)
        {
            LogStateFormatterCache<TState>.Format(state, buffer);
        }
        else
        {
            WriteString(buffer, formatter(state, exception));
        }

        // 8. Exception Formatting
        if (exception != null)
        {
            buffer.Write("\n"u8);
            if (useColors) buffer.Write("\x1b[31m"u8);
            WriteString(buffer, exception.ToString());
            if (useColors) buffer.Write("\x1b[0m"u8);
        }

        buffer.Write("\n"u8);
    }

    private static void WriteTimestamp(IBufferWriter<byte> buffer, SimpleConsoleFormatterOptions? options, bool useColors)
    {
        if (string.IsNullOrEmpty(options?.TimestampFormat)) return;

        if (useColors) buffer.Write("\x1b[90m"u8);

        var dt = options?.UseUtcTimestamp ?? false ? DateTime.UtcNow : DateTime.Now;
        Span<char> timeSpan = stackalloc char[64];

        // Pass the format string natively!
        if (dt.TryFormat(timeSpan, out int charsWritten, options?.TimestampFormat))
        {
            int maxTimeBytes = System.Text.Encoding.UTF8.GetMaxByteCount(charsWritten);
            int timeBytes = System.Text.Encoding.UTF8.GetBytes(timeSpan[..charsWritten], buffer.GetSpan(maxTimeBytes));
            buffer.Advance(timeBytes);
        }

        if (useColors) buffer.Write("\x1b[0m"u8);
    }

    private static void WriteLogLevel(IBufferWriter<byte> buffer, LogLevel logLevel, bool useColors)
    {
        if (useColors)
        {
            var levelBytes = logLevel switch
            {
                LogLevel.Trace => "\x1b[90mtrce\x1b[0m: "u8,
                LogLevel.Debug => "\x1b[90mdbug\x1b[0m: "u8,
                LogLevel.Information => "\x1b[32minfo\x1b[0m: "u8,
                LogLevel.Warning => "\x1b[33mwarn\x1b[0m: "u8,
                LogLevel.Error => "\x1b[31mfail\x1b[0m: "u8,
                LogLevel.Critical => "\x1b[37;41mcrit\x1b[0m: "u8,
                _ => "\x1b[32minfo\x1b[0m: "u8
            };
            buffer.Write(levelBytes);
        }
        else
        {
            var levelBytes = logLevel switch
            {
                LogLevel.Trace => "trce: "u8,
                LogLevel.Debug => "dbug: "u8,
                LogLevel.Information => "info: "u8,
                LogLevel.Warning => "warn: "u8,
                LogLevel.Error => "fail: "u8,
                LogLevel.Critical => "crit: "u8,
                _ => "info: "u8
            };
            buffer.Write(levelBytes);
        }
    }

    private static void WriteScopes(IBufferWriter<byte> buffer, SimpleConsoleFormatterOptions? options)
    {
        WriteScopeNode(buffer, ScopeContext.Current!, options);
    }

    private static void WriteScopeNode(IBufferWriter<byte> buffer, ScopeNode node, SimpleConsoleFormatterOptions? options)
    {
        if (!options?.IncludeScopes ?? true) return;

        if (node.Parent != null) WriteScopeNode(buffer, node.Parent, options);
        buffer.Write("=> "u8);
        buffer.Write(node.WrittenSpan);
        buffer.Write(" "u8);
    }

    private static void WriteString(IBufferWriter<byte> buffer, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(value.Length);
        int written = System.Text.Encoding.UTF8.GetBytes(value, buffer.GetSpan(maxBytes));
        buffer.Advance(written);
    }

    public void Dispose()
    {
        _optionsReloadToken?.Dispose();
    }
}

internal static class LogStateFormatterCache<TState>
{
    public static readonly Action<TState, IBufferWriter<byte>>? Format;

    static LogStateFormatterCache()
    {
        // 1. Check if the type implements our formatter without casting it
        if (typeof(IDeferredUtf8Formatter).IsAssignableFrom(typeof(TState)))
        {
            // 2. Create a closed generic delegate to the constrained method below
            var method = typeof(LogStateFormatterCache<TState>)
                .GetMethod(nameof(FormatInternal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(TState));

            Format = (Action<TState, IBufferWriter<byte>>)Delegate.CreateDelegate(typeof(Action<TState, IBufferWriter<byte>>), method);
        }
    }

    // Because this method is constrained (where T : ...), calling FormatTo does NOT box the struct!
    private static void FormatInternal<T>(T state, IBufferWriter<byte> writer) where T : IDeferredUtf8Formatter
    {
        state.FormatTo(writer);
    }
}

