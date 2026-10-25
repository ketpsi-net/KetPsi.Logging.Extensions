using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.Logging;

namespace Generator;

/// <summary>
/// End-to-end tests: every log call below is rewritten by the real source generator at compile time,
/// so these cover the generated code as well as the runtime pieces it relies on.
/// </summary>
public class InterceptedLoggingTests
{
    // A private nested reference type cannot be named by generated code; it is read back as object.
    private sealed class Secret
    {
        public string Code { get; set; } = "s3cr3t";
        public override string ToString() => Code;
    }

    private sealed record Item(int Id);

    [Fact]
    public void Literal_WithQuotesBackslashesAndNewlines_IsEscaped()
    {
        var logger = new RecordingLogger();
        int x = 5;

        logger.LogInformation($"quote \" backslash \\ tab \t newline \n value {x:@X}");

        Assert.Equal("quote \" backslash \\ tab \t newline \n value 5", logger.MessageText);
        Assert.Equal("quote \" backslash \\ tab \t newline \n value {X}", logger.Template);
    }

    [Fact]
    public void LiteralBraces_StayEscapedInTemplate()
    {
        var logger = new RecordingLogger();
        int x = 7;

        logger.LogInformation($"{{literal}} {x:@X}");

        Assert.Equal("{literal} 7", logger.MessageText);
        Assert.Equal("{{literal}} {X}", logger.Template);
    }

    [Fact]
    public void DuplicatePropertyNames_Compile_AndKeepBothValues()
    {
        var logger = new RecordingLogger();
        var a = new Item(1);
        var b = new Item(2);

        logger.LogInformation($"{a.Id} and {b.Id}");

        Assert.Equal("1 and 2", logger.MessageText);
        Assert.Equal([("Id", "1"), ("Id", "2")], logger.Parameters);
    }

    [Fact]
    public void UnnamedHole_DerivesNameFromExpression()
    {
        var logger = new RecordingLogger();
        int count = 3;

        logger.LogInformation($"count is {count}");

        Assert.Equal("count is {count}", logger.Template);
        Assert.Equal([("count", "3")], logger.Parameters);
    }

    [Fact]
    public void PrivateNestedReferenceType_IsLoggedAsObject()
    {
        var logger = new RecordingLogger();
        var secret = new Secret();

        logger.LogInformation($"secret {secret:@Secret} json {secret:@Json:json}");

        Assert.Equal("secret s3cr3t json {\"Code\":\"s3cr3t\"}", logger.MessageText);
    }

    [Fact]
    public void LargeStructs_GrowTheHandlerBuffer()
    {
        var logger = new RecordingLogger();
        var w1 = new Wide(1);
        var w2 = new Wide(2);
        var w3 = new Wide(3);

        // 3 x 48 bytes exceeds the initial 32-bytes-per-hole estimate.
        logger.LogInformation($"{w1:@W1} {w2:@W2} {w3:@W3}");

        Assert.Equal("W1 W2 W3", logger.MessageText);
    }

    [Fact]
    public void EventIdOverloads_AreIntercepted()
    {
        var logger = new RecordingLogger();
        var ex = new InvalidOperationException("boom");
        int id = 9;

        logger.LogWarning(new EventId(42, "evt"), $"warn {id:@Id}");
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Equal(42, logger.EventId.Id);
        Assert.Equal("evt", logger.EventId.Name);
        Assert.Equal("warn 9", logger.MessageText);

        logger.LogError(7, ex, $"error {id:@Id}");
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Equal(7, logger.EventId.Id);
        Assert.Same(ex, logger.Exception);
    }

    [Fact]
    public void ExceptionOverload_PassesException()
    {
        var logger = new RecordingLogger();
        var ex = new InvalidOperationException("boom");

        logger.LogCritical(ex, $"critical {1:@One}");

        Assert.Equal(LogLevel.Critical, logger.Level);
        Assert.Same(ex, logger.Exception);
        Assert.Equal(0, logger.EventId.Id);
    }

    [Fact]
    public void Mask_CombinedWithStandardFormat()
    {
        var logger = new RecordingLogger();
        int n = 42;

        logger.LogInformation($"{n:@N:D6:mask:..2}");

        Assert.Equal("**0042", logger.MessageText);
    }

    [Fact]
    public void Mask_SingleNumber_KeepsLastN()
    {
        var logger = new RecordingLogger();
        string card = "4532123412341092";

        logger.LogInformation($"{card:@Card:mask:4}");

        Assert.Equal("************1092", logger.MessageText);
    }

    [Fact]
    public void Format_WithColons_IsNotMistakenForMask()
    {
        var logger = new RecordingLogger();
        var time = new DateTime(2024, 1, 2, 13, 14, 15);

        logger.LogInformation($"{time:@Time:HH:mm:ss}");

        Assert.Equal("13:14:15", logger.MessageText);
    }

    [Fact]
    public void Alignment_PadsRenderedValue()
    {
        var logger = new RecordingLogger();
        int x = 42;

        logger.LogInformation($"[{x,5:@Right}][{x,-5:@Left}]");

        Assert.Equal("[   42][42   ]", logger.MessageText);
    }

    [Fact]
    public void ConcatenatedInterpolatedStrings_AreIntercepted()
    {
        var logger = new RecordingLogger();
        int x = 1, y = 2;

        logger.LogInformation($"a {x:@X} " + $"b {y:@Y}");

        Assert.Equal("a 1 b 2", logger.MessageText);
        Assert.Equal("a {X} b {Y}", logger.Template);
    }

    [Fact]
    public void StartScope_DispatchesGeneratedState()
    {
        var logger = new RecordingLogger();
        string tenant = "acme";

        using (logger.StartScope($"Tenant {tenant:@TenantId}"))
        {
        }

        Assert.Equal("Tenant acme", logger.ScopeText);
    }

    [Fact]
    public void NonKetPsiProviders_ReceiveRenderedMessage()
    {
        var logger = new FormatterOnlyLogger();
        int x = 5;

        logger.LogInformation($"value {x:@X}");

        Assert.Equal("value 5", logger.Formatted);
    }

    [Fact]
    public void DisabledLevel_DoesNotCallLogger()
    {
        var logger = new RecordingLogger { MinLevel = LogLevel.Warning };

        logger.LogDebug($"hidden {1:@One}");

        Assert.Equal(0, logger.Calls);
    }

    [Fact]
    public void ClassesWithSameSimpleName_InDifferentNamespaces_BothLog()
    {
        var logger = new RecordingLogger();

        NsA.Duplicate.Log(logger);
        Assert.Equal("from A", logger.MessageText);

        NsB.Duplicate.Log(logger);
        Assert.Equal("from B", logger.MessageText);
    }

    [Fact]
    public void InterceptedCalls_DoNotAllocate()
    {
        var logger = new RecordingLogger { Capture = false, RunOtlpEncoding = true };
        int count = 12345;
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        decimal amount = 1500.75m;
        string user = "alice";
        int? maybe = 5;
        var w1 = new Wide(1);
        var w2 = new Wide(2);
        var w3 = new Wide(3);
        var eventId = new EventId(1, "evt");

        void LogAll()
        {
            logger.LogInformation($"count {count:@Count:N0} id {id:@Id} amount {amount:@Amount} user {user:@User}");
            logger.LogWarning(eventId, $"masked {id:@Masked:mask:4..8} maybe {maybe:@Maybe} aligned {count,8:@Aligned}");
            logger.LogInformation($"{w1:@W1} {w2:@W2} {w3:@W3}");
        }

        for (int i = 0; i < 200; i++) LogAll();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) LogAll();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(logger.LastOtlpSize > 0);
    }

    /// <summary>A 48-byte unmanaged struct that formats without allocating.</summary>
    public readonly struct Wide(long a) : ISpanFormattable, IUtf8SpanFormattable
    {
        public readonly long A = a, B, C, D, E, F;

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            charsWritten = 0;
            if (destination.Length < 1 || !A.TryFormat(destination[1..], out int n, default, CultureInfo.InvariantCulture)) return false;
            destination[0] = 'W';
            charsWritten = n + 1;
            return true;
        }

        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            bytesWritten = 0;
            if (utf8Destination.Length < 1 || !A.TryFormat(utf8Destination[1..], out int n, default, CultureInfo.InvariantCulture)) return false;
            utf8Destination[0] = (byte)'W';
            bytesWritten = n + 1;
            return true;
        }

        public string ToString(string? format, IFormatProvider? formatProvider) => "W" + A;
        public override string ToString() => "W" + A;
    }
}

/// <summary>A KetPsi-aware logger that renders generated states through the dispatcher.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ArrayBufferWriter<byte> _message = new(1024);
    private readonly ArrayBufferWriter<byte> _scope = new(256);

    public LogLevel MinLevel { get; set; } = LogLevel.Trace;
    public bool Capture { get; set; } = true;
    public bool RunOtlpEncoding { get; set; }

    public int Calls { get; private set; }
    public LogLevel Level { get; private set; }
    public EventId EventId { get; private set; }
    public Exception? Exception { get; private set; }
    public string? Template { get; private set; }
    public int LastOtlpSize { get; private set; }
    public List<(string Name, string Value)> Parameters { get; } = [];

    public string MessageText => Encoding.UTF8.GetString(_message.WrittenSpan);
    public string ScopeText => Encoding.UTF8.GetString(_scope.WrittenSpan);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        _scope.ResetWrittenCount();
        if (!StructuredLogStateDispatcher.TryFormatTo(ref state, _scope))
            throw new InvalidOperationException("Scope state was not a registered generated state.");
        return null;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Calls++;
        Level = logLevel;
        EventId = eventId;
        Exception = exception;
        _message.ResetWrittenCount();

        var visitor = new Visitor(this);
        if (!StructuredLogStateDispatcher.TryAccept(ref state, ref visitor))
            throw new InvalidOperationException("Log state was not a registered generated state.");
    }

    private readonly struct Visitor(RecordingLogger owner) : IStructuredLogStateVisitor
    {
        public unsafe void Visit<TState>(ref TState state) where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
        {
            state.FormatTo(owner._message);

            if (owner.Capture)
            {
                owner.Template = state.OriginalFormat.ToString();
                owner.Parameters.Clear();
                var collector = new ParameterCollector(owner.Parameters);
                state.WriteParameters(ref collector);
            }

            if (owner.RunOtlpEncoding)
            {
                // Exercise the OTLP attribute size + encode paths over the same generated state.
                var calculator = new OtlpParameterSizeCalculator();
                state.WriteParameters(ref calculator);

                Span<byte> buffer = stackalloc byte[4096];
                fixed (byte* ptr = buffer)
                {
                    var encoder = new OtlpParameterEncoder(ptr, 0, buffer.Length);
                    state.WriteParameters(ref encoder);
                }
                owner.LastOtlpSize = calculator.TotalSize;
            }
        }
    }

    private readonly struct ParameterCollector(List<(string Name, string Value)> parameters) : ILogParameterWriter
    {
        public void Write<T>(in LogParameter<T> parameter)
        {
            var buffer = new ArrayBufferWriter<byte>();
            parameter.FormatTo(buffer);
            parameters.Add((parameter.Name!, Encoding.UTF8.GetString(buffer.WrittenSpan)));
        }
    }
}

/// <summary>Behaves like a provider that knows nothing about KetPsi and only calls the MEL formatter.</summary>
internal sealed class FormatterOnlyLogger : ILogger
{
    public string? Formatted { get; private set; }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Formatted = formatter(state, exception);
}
