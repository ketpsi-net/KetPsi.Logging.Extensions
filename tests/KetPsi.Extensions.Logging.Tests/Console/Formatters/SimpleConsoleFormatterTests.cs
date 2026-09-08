using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.Console.Internals.Formatters;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Console.Formatters;

// ---------------------------------------------------------------------
// Test double that works with internal options types
// ---------------------------------------------------------------------
internal sealed class TestOptionsMonitor : IOptionsMonitor<ZeroAllocConsoleFormatterOptions>
{
    private ZeroAllocConsoleFormatterOptions _current;
    private Action<ZeroAllocConsoleFormatterOptions, string?>? _listener;

    public TestOptionsMonitor(ZeroAllocConsoleFormatterOptions current)
        => _current = current;

    public ZeroAllocConsoleFormatterOptions CurrentValue => _current;

    public ZeroAllocConsoleFormatterOptions Get(string? name) => _current;

    public IDisposable OnChange(Action<ZeroAllocConsoleFormatterOptions, string?> listener)
    {
        _listener = listener;
        return new NoopDisposable();
    }

    public void Change(ZeroAllocConsoleFormatterOptions newOptions)
    {
        _current = newOptions;
        _listener?.Invoke(newOptions, null);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public class SimpleConsoleFormatterTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    private static ArrayBufferWriter<byte> CreateBuffer(int capacity = 2048)
        => new(capacity);

    private static string GetString(ArrayBufferWriter<byte> buffer)
        => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string StripAnsi(string input)
        => Regex.Replace(input, @"\x1B\[[0-9;]*m", "");

    private static ZeroAllocConsoleFormatterOptions Defaults(
        Action<ZeroAllocConsoleFormatterOptions>? configure = null)
    {
        var opts = new ZeroAllocConsoleFormatterOptions
        {
            IncludeCategory = false,
            IncludeEventId = false,
            IncludeScopes = false,
            SingleLine = true,
            ColorBehavior = LoggerColorBehavior.Disabled,
            TimestampFormat = null,
            UseUtcTimestamp = false
        };
        configure?.Invoke(opts);
        return opts;
    }

    private SimpleConsoleFormatter CreateFormatter(
        ZeroAllocConsoleFormatterOptions options,
        out TestOptionsMonitor monitor)
    {
        monitor = new TestOptionsMonitor(options);
        var formatter = new SimpleConsoleFormatter(monitor);
        _disposables.Add(formatter);
        return formatter;
    }

    private SimpleConsoleFormatter CreateFormatter(ZeroAllocConsoleFormatterOptions options)
        => CreateFormatter(options, out _);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    // =====================================================================
    // Core message / level tests
    // =====================================================================

    [Theory]
    [InlineData(LogLevel.Trace, "trce:")]
    [InlineData(LogLevel.Debug, "dbug:")]
    [InlineData(LogLevel.Information, "info:")]
    [InlineData(LogLevel.Warning, "warn:")]
    [InlineData(LogLevel.Error, "fail:")]
    [InlineData(LogLevel.Critical, "crit:")]
    public void Format_AllLogLevels_CorrectPrefix(LogLevel level, string expected)
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();

        f.Format(buf, "Cat", level, default, "msg", null, static (s, _) => s);

        Assert.Contains(expected, StripAnsi(GetString(buf)));
    }

    [Fact]
    public void Format_SimpleMessage_EndsWithNewline()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, default, "hello", null, static (s, _) => s);

        Assert.EndsWith("\n", GetString(buf));
    }

    // =====================================================================
    // Option matrix – IncludeCategory / SingleLine
    // =====================================================================

    [Theory]
    [InlineData(true, true)]   // category + single-line
    [InlineData(true, false)]  // category + multi-line
    [InlineData(false, true)]  // no category
    [InlineData(false, false)]
    public void Format_CategoryAndSingleLine_Combinations(bool includeCategory, bool singleLine)
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.IncludeCategory = includeCategory;
            o.SingleLine = singleLine;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "My.Category", LogLevel.Information, default, "payload", null, static (s, _) => s);
        var output = StripAnsi(GetString(buf));

        if (includeCategory)
        {
            Assert.Contains("My.Category", output);

            if (singleLine)
            {
                Assert.Contains("My.Category payload", output);
                Assert.DoesNotContain("My.Category\n", output);
            }
            else
            {
                // category on its own line, message indented with 6 spaces
                Assert.Contains("My.Category\n      payload", output);
            }
        }
        else
        {
            Assert.DoesNotContain("My.Category", output);
            Assert.Contains("payload", output);
        }
    }

    // =====================================================================
    // Option matrix – IncludeEventId
    // =====================================================================

    [Theory]
    [InlineData(true, 42, true)]    // enabled + non-zero → present
    [InlineData(true, 0, false)]    // enabled + zero → absent
    [InlineData(false, 99, false)]  // disabled → absent
    public void Format_EventId_Combinations(bool includeEventId, int id, bool shouldAppear)
    {
        var f = CreateFormatter(Defaults(o => o.IncludeEventId = includeEventId));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, new EventId(id), "msg", null, static (s, _) => s);
        var output = GetString(buf);

        if (shouldAppear)
            Assert.Contains($"[{id}] ", output);
        else
            Assert.DoesNotContain($"[{id}]", output);
    }

    // =====================================================================
    // Option matrix – Timestamp
    // =====================================================================

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("HH:mm:ss", true)]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", true)]
    [InlineData("O", true)]
    public void Format_TimestampFormat_Combinations(string? format, bool shouldAppear)
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.TimestampFormat = format;
            o.UseUtcTimestamp = false;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, default, "msg", null, static (s, _) => s);
        var output = StripAnsi(GetString(buf));

        if (shouldAppear)
        {
            // Must contain at least one digit sequence that looks like a time component
            Assert.Matches(@"\d", output);
            Assert.False(output.StartsWith("info:")); // timestamp comes first
        }
        else
        {
            Assert.StartsWith("info:", output);
        }
    }

    [Fact]
    public void Format_UseUtcTimestamp_True_DoesNotThrow()
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.TimestampFormat = "O";
            o.UseUtcTimestamp = true;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Debug, default, "msg", null, static (s, _) => s);
        Assert.True(buf.WrittenCount > 0);
    }

    // =====================================================================
    // Option matrix – ColorBehavior
    // =====================================================================

    [Theory]
    [InlineData(LoggerColorBehavior.Disabled, false)]
    [InlineData(LoggerColorBehavior.Enabled, true)]
    public void Format_ColorBehavior_Combinations(LoggerColorBehavior behavior, bool expectAnsi)
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.ColorBehavior = behavior;
            o.IncludeCategory = true;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Error, default, "msg", null, static (s, _) => s);
        var raw = GetString(buf);

        if (expectAnsi)
        {
            Assert.Contains("\x1b[", raw);
            Assert.Contains("\x1b[31m", raw); // error red
            Assert.Contains("\x1b[0m", raw);
        }
        else
        {
            Assert.DoesNotContain("\x1b[", raw);
        }
    }

    [Theory]
    [InlineData(LogLevel.Trace, "\x1b[90m")]
    [InlineData(LogLevel.Debug, "\x1b[90m")]
    [InlineData(LogLevel.Information, "\x1b[32m")]
    [InlineData(LogLevel.Warning, "\x1b[33m")]
    [InlineData(LogLevel.Error, "\x1b[31m")]
    [InlineData(LogLevel.Critical, "\x1b[37;41m")]
    public void Format_ColorBehaviorEnabled_CorrectColorPerLevel(LogLevel level, string expectedAnsi)
    {
        var f = CreateFormatter(Defaults(o => o.ColorBehavior = LoggerColorBehavior.Enabled));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", level, default, "msg", null, static (s, _) => s);
        Assert.Contains(expectedAnsi, GetString(buf));
    }

    // =====================================================================
    // Full option combination – realistic “production-like” profiles
    // =====================================================================

    [Fact]
    public void Format_FullVerboseProfile()
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.IncludeCategory = true;
            o.IncludeEventId = true;
            o.IncludeScopes = false;          // scopes need ScopeContext – tested separately if available
            o.SingleLine = false;
            o.ColorBehavior = LoggerColorBehavior.Disabled;
            o.TimestampFormat = "HH:mm:ss";
            o.UseUtcTimestamp = true;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "MyApp.Service", LogLevel.Warning, new EventId(1001, "Slow"), "took too long", null,
            static (s, _) => s);

        var output = StripAnsi(GetString(buf));

        Assert.Matches(@"\d{2}:\d{2}:\d{2}", output);   // timestamp
        Assert.Contains("warn:", output);
        Assert.Contains("[1001] ", output);
        Assert.Contains("MyApp.Service", output);
        Assert.Contains("took too long", output);
        Assert.Contains("\n      ", output);              // multi-line indent
    }

    [Fact]
    public void Format_MinimalProfile()
    {
        var f = CreateFormatter(Defaults(o =>
        {
            o.IncludeCategory = false;
            o.IncludeEventId = false;
            o.IncludeScopes = false;
            o.SingleLine = true;
            o.ColorBehavior = LoggerColorBehavior.Disabled;
            o.TimestampFormat = null;
        }));
        var buf = CreateBuffer();

        f.Format(buf, "Ignored.Category", LogLevel.Information, new EventId(42), "only-message", null,
            static (s, _) => s);

        var output = GetString(buf);
        Assert.Equal("info: only-message\n", output);
    }

    // =====================================================================
    // Exception
    // =====================================================================

    [Fact]
    public void Format_WithException_AppendsException()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();
        var ex = new InvalidOperationException("boom");

        f.Format(buf, "Cat", LogLevel.Error, default, "failed", ex, static (s, _) => s);

        var output = GetString(buf);
        Assert.Contains("failed", output);
        Assert.Contains("InvalidOperationException", output);
        Assert.Contains("boom", output);
    }

    [Fact]
    public void Format_WithExceptionAndColors_UsesRed()
    {
        var f = CreateFormatter(Defaults(o => o.ColorBehavior = LoggerColorBehavior.Enabled));
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Error, default, "msg", new Exception("x"), static (s, _) => s);

        Assert.Contains("\x1b[31m", GetString(buf));
    }

    // =====================================================================
    // IDeferredUtf8Formatter fast-path vs fallback
    // =====================================================================

    private readonly struct DeferredMsg : IDeferredUtf8Formatter
    {
        private readonly string _m;
        public DeferredMsg(string m) => _m = m;
        public void FormatTo(IBufferWriter<byte> w)
        {
            var b = Encoding.UTF8.GetBytes(_m);
            b.CopyTo(w.GetSpan(b.Length));
            w.Advance(b.Length);
        }
    }

    [Fact]
    public void Format_IDeferredUtf8Formatter_UsesCache_NotFallback()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();
        bool fallbackCalled = false;

        f.Format(buf, "Cat", LogLevel.Information, default, new DeferredMsg("zero-alloc"), null,
            (s, _) => { fallbackCalled = true; return "FALLBACK"; });

        Assert.Contains("zero-alloc", GetString(buf));
        Assert.DoesNotContain("FALLBACK", GetString(buf));
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void Format_OrdinaryState_UsesFallbackFormatter()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, default, "plain", null,
            static (s, _) => $"FMT:{s}");

        Assert.Contains("FMT:plain", GetString(buf));
    }

    // =====================================================================
    // Options reload
    // =====================================================================

    [Fact]
    public void OptionsReload_TakesEffectImmediately()
    {
        var initial = Defaults(o => o.IncludeCategory = false);
        var f = CreateFormatter(initial, out var monitor);

        var buf1 = CreateBuffer();
        f.Format(buf1, "Cat", LogLevel.Information, default, "msg", null, static (s, _) => s);
        Assert.DoesNotContain("Cat", GetString(buf1));

        monitor.Change(Defaults(o =>
        {
            o.IncludeCategory = true;
            o.SingleLine = true;
        }));

        var buf2 = CreateBuffer();
        f.Format(buf2, "Cat", LogLevel.Information, default, "msg", null, static (s, _) => s);
        Assert.Contains("Cat", GetString(buf2));
    }

    // =====================================================================
    // Dispose
    // =====================================================================

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var monitor = new TestOptionsMonitor(Defaults());
        var f = new SimpleConsoleFormatter(monitor);
        f.Dispose(); // should be safe to call
        f.Dispose(); // and idempotent enough
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Fact]
    public void Format_EmptyMessage_StillProducesLevelAndNewline()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, default, "", null, static (s, _) => s);

        var output = GetString(buf);
        Assert.Contains("info:", output);
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public void Format_NullFormatterResult_DoesNotThrow()
    {
        var f = CreateFormatter(Defaults());
        var buf = CreateBuffer();

        f.Format(buf, "Cat", LogLevel.Information, default, "state", null, static (_, _) => null!);
        Assert.True(buf.WrittenCount > 0);
    }
}