using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using KetPsi.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal sealed class ZeroAllocOtlpLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ZeroAllocOtlpLoggerProvider _provider;
    private readonly byte[] _scopeBytes;
    private readonly byte[] _categoryUtf8;

    // Per-thread body buffer: allocating a writer per log would break the zero-allocation hot path.
    [ThreadStatic]
    private static PooledByteBufferWriter? t_bodyWriter;
    [ThreadStatic]
    private static PooledByteBufferWriter? t_valueArena;
    [ThreadStatic]
    private static int[]? t_valueRanges;

    // Take-ownership like the writers: a value whose ToString logs on this thread gets its own array
    // instead of overwriting the outer call's ranges.
    private static int[] RentValueRanges(int length)
    {
        var ranges = t_valueRanges;
        t_valueRanges = null;
        return ranges is not null && ranges.Length >= length ? ranges : new int[Math.Max(length, 32)];
    }

    public OtlpBatcher Batcher => _provider.Batcher;
    public string CategoryName => _categoryName;

    public ZeroAllocOtlpLogger(string categoryName, ZeroAllocOtlpLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
        // scope.name = logger category (matches standard OTEL / Microsoft exporter)
        _scopeBytes = provider.Batcher.GetOrCreateScopeBytes(categoryName);
        _categoryUtf8 = System.Text.Encoding.UTF8.GetBytes(categoryName);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _provider.ScopeProvider?.Push(state) ?? NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        // Generated KetPsi states are dispatched with their concrete type (no boxing, no reflection; NativeAOT-safe).
        var visitor = new StructuredStateVisitor(this, logLevel, eventId, exception);
        if (StructuredLogStateDispatcher.TryAccept(ref state, ref visitor)) return;

        ProcessFallback(logLevel, eventId, state, exception, formatter);
    }

    private readonly struct StructuredStateVisitor(
        ZeroAllocOtlpLogger logger, LogLevel logLevel, EventId eventId, Exception? exception) : IStructuredLogStateVisitor
    {
        public void Visit<TState>(ref TState state) where TState : struct, IStructuredLogState, IDeferredUtf8Formatter
            => logger.ProcessStructured(logLevel, eventId, ref state, exception);
    }

    internal unsafe void ProcessStructured<TStructuredState>(
        LogLevel logLevel,
        EventId eventId,
        ref TStructuredState structuredState,
        Exception? exception)
        where TStructuredState : IStructuredLogState, IDeferredUtf8Formatter
    {
        long timeUnixNano = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        int severityNumber = MapLogLevelToOtlpSeverity(logLevel);
        ReadOnlySpan<byte> severityText = MapLogLevelToSeverityTextUtf8(logLevel);

        byte[]? pooledIds = null;
        Span<byte> traceId = default;
        Span<byte> spanId = default;

        if (Activity.Current is not null)
        {
            pooledIds = ArrayPool<byte>.Shared.Rent(24);
            traceId = pooledIds.AsSpan(0, 16);
            spanId = pooledIds.AsSpan(16, 8);
            Activity.Current.TraceId.CopyTo(traceId);
            Activity.Current.SpanId.CopyTo(spanId);
        }

        PooledByteBufferWriter? bodyWriter = null;
        int[]? valueRanges = null;
        // Values not taken from the body are formatted to UTF-8 once (size pass) and replayed by the encoder.
        PooledByteBufferWriter valueArena = PooledByteBufferWriter.Rent(ref t_valueArena);
        try
        {
            // Render first, recording where each value lands: attributes reuse those bytes instead of
            // formatting every value a second time.
            bodyWriter = PooledByteBufferWriter.Rent(ref t_bodyWriter);
            valueRanges = RentValueRanges(2 * structuredState.ParameterCount);
            structuredState.FormatTo(bodyWriter, valueRanges);
            ReadOnlySpan<byte> bodyUtf8 = bodyWriter.WrittenSpan;
            var bodyValues = new BodyValues(bodyWriter, valueRanges);

            var sizeCalculator = new OtlpParameterSizeCalculator { Arena = valueArena, Body = bodyValues };
            structuredState.WriteParameters(ref sizeCalculator);

            // Standard enrichment attributes (category, event, exception, original format template)
            int enrichmentSize = 0;
            // Constant keys, the category and the generated template are already UTF-8.
            enrichmentSize += OtlpAttributeFormatter.CalculateStringSize("logger"u8, _categoryUtf8, 6);
            // Keep the message template under the conventional .NET key
            enrichmentSize += OtlpAttributeFormatter.CalculateStringSize("{OriginalFormat}"u8, structuredState.OriginalFormatUtf8, 6);
            if (eventId.Id != 0)
                enrichmentSize += OtlpAttributeFormatter.CalculateInt64Size("event.id"u8, eventId.Id, 6);
            if (!string.IsNullOrEmpty(eventId.Name))
                enrichmentSize += OtlpAttributeFormatter.CalculateStringSize("event.name".AsSpan(), eventId.Name.AsSpan(), 6);
            if (exception is not null)
            {
                enrichmentSize += OtlpAttributeFormatter.CalculateStringSize("exception.type".AsSpan(), (exception.GetType().FullName ?? exception.GetType().Name).AsSpan(), 6);
                enrichmentSize += OtlpAttributeFormatter.CalculateStringSize("exception.message".AsSpan(), (exception.Message ?? string.Empty).AsSpan(), 6);
            }

            int scopeSize = CalculateScopeAttributesSize();
            int attributesSize = sizeCalculator.TotalSize + enrichmentSize + scopeSize;

            string? eventName = eventId.Name;
            int internalSize = OtlpLogRecordFormatter.CalculateLogRecordSizeUtf8Body(
                timeUnixNano, severityNumber, severityText, bodyUtf8.Length, attributesSize, traceId, spanId, eventName);

            int lengthBytes = 1;
            ulong uSize = (ulong)internalSize;
            while (uSize >= 128)
            {
                lengthBytes++;
                uSize >>= 7;
            }
            int recordSize = 1 + lengthBytes + internalSize;

            Span<byte> reservedSpan = Batcher.Reserve(recordSize, _scopeBytes, out BufferState bufferState);

            try
            {
                var writer = new ZeroAllocProtobufWriter(reservedSpan);

                writer.WriteTag(2, 2);
                writer.WriteVarInt((ulong)internalSize);

                OtlpLogRecordFormatter.WriteLogRecordUtf8Body(
                    ref writer, timeUnixNano, severityNumber, severityText, bodyUtf8, traceId, spanId, eventName);

                // Enrichment attributes first (advances writer.BytesWritten)
                OtlpAttributeFormatter.WriteString(ref writer, "logger"u8, _categoryUtf8, 6);
                OtlpAttributeFormatter.WriteString(ref writer, "{OriginalFormat}"u8, structuredState.OriginalFormatUtf8, 6);
                if (eventId.Id != 0)
                    OtlpAttributeFormatter.WriteInt64(ref writer, "event.id"u8, eventId.Id, 6);
                if (!string.IsNullOrEmpty(eventId.Name))
                    OtlpAttributeFormatter.WriteString(ref writer, "event.name".AsSpan(), eventId.Name.AsSpan(), 6);
                if (exception is not null)
                {
                    OtlpAttributeFormatter.WriteString(ref writer, "exception.type".AsSpan(), (exception.GetType().FullName ?? exception.GetType().Name).AsSpan(), 6);
                    OtlpAttributeFormatter.WriteString(ref writer, "exception.message".AsSpan(), (exception.Message ?? string.Empty).AsSpan(), 6);
                }

                // Structured parameters (field 6) written directly after enrichment
                int attrOffset = writer.BytesWritten;
                if (sizeCalculator.TotalSize > 0)
                {
                    fixed (byte* ptr = reservedSpan)
                    {
                        var encoder = new OtlpParameterEncoder(ptr, attrOffset, reservedSpan.Length, valueArena, bodyValues);
                        structuredState.WriteParameters(ref encoder);
                    }
                    attrOffset += sizeCalculator.TotalSize;
                }

                // Active scopes (StartScope / BeginScope) — last so we need not advance writer
                if (scopeSize > 0)
                {
                    fixed (byte* ptr = reservedSpan)
                    {
                        WriteScopeAttributes(ptr, attrOffset, reservedSpan.Length);
                    }
                }
            }
            finally
            {
                Batcher.Commit(bufferState);
            }
        }
        finally
        {
            if (bodyWriter != null) PooledByteBufferWriter.Return(ref t_bodyWriter, bodyWriter);
            if (valueRanges != null) t_valueRanges = valueRanges;
            PooledByteBufferWriter.Return(ref t_valueArena, valueArena);
            if (pooledIds != null)
            {
                ArrayPool<byte>.Shared.Return(pooledIds);
            }
        }
    }

    private unsafe void ProcessFallback<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string? originalFormat = null;
        var stateParams = state as System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, object?>>;

        int fallbackAttributesSize = 0;

        if (stateParams != null)
        {
            for (int i = 0; i < stateParams.Count; i++)
            {
                var kvp = stateParams[i];
                if (kvp.Key == "{OriginalFormat}")
                {
                    originalFormat = kvp.Value?.ToString();
                    continue;
                }

                string strVal = kvp.Value?.ToString() ?? string.Empty;
                int keyBytes = System.Text.Encoding.UTF8.GetByteCount(kvp.Key);
                int valueBytes = System.Text.Encoding.UTF8.GetByteCount(strVal);

                // 1. Size of the string value
                int stringValueFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueBytes) + valueBytes;

                // 2. Size of the AnyValue wrapper (THIS WAS MISSING THE 1+VarInt OVERHEAD BEFORE)
                int anyValuePayloadSize = stringValueFieldSize;
                int anyValueFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize;

                // 3. Size of the Key field
                int keyFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)keyBytes) + keyBytes;

                // 4. Total Size of the KeyValue Payload
                int kvpPayloadSize = keyFieldSize + anyValueFieldSize;

                // 5. Add to total attributes size (Tag 6 + VarInt Length + Payload)
                fallbackAttributesSize += 1 + ProtobufMath.GetVarIntSize((ulong)kvpPayloadSize) + kvpPayloadSize;
            }
        }

        // Enrichment attributes
        fallbackAttributesSize += OtlpAttributeFormatter.CalculateStringSize("logger".AsSpan(), _categoryName.AsSpan(), 6);
        if (eventId.Id != 0)
            fallbackAttributesSize += OtlpAttributeFormatter.CalculateInt64Size("event.id".AsSpan(), eventId.Id, 6);
        if (!string.IsNullOrEmpty(eventId.Name))
            fallbackAttributesSize += OtlpAttributeFormatter.CalculateStringSize("event.name".AsSpan(), eventId.Name.AsSpan(), 6);
        if (exception is not null)
        {
            fallbackAttributesSize += OtlpAttributeFormatter.CalculateStringSize("exception.type".AsSpan(), (exception.GetType().FullName ?? exception.GetType().Name).AsSpan(), 6);
            fallbackAttributesSize += OtlpAttributeFormatter.CalculateStringSize("exception.message".AsSpan(), (exception.Message ?? string.Empty).AsSpan(), 6);
        }

        int scopeSize = CalculateScopeAttributesSize();
        fallbackAttributesSize += scopeSize;

        // Prefer the rendered message for body; keep template available via attributes / originalFormat.
        string bodyString = formatter(state, exception);
        if (string.IsNullOrEmpty(bodyString))
            bodyString = originalFormat ?? string.Empty;
        if (string.IsNullOrEmpty(bodyString) && exception is null) return;

        // If we have a template distinct from the rendered body, ensure it is exported as {OriginalFormat}.
        if (!string.IsNullOrEmpty(originalFormat) && !string.Equals(originalFormat, bodyString, StringComparison.Ordinal))
        {
            fallbackAttributesSize += OtlpAttributeFormatter.CalculateStringSize("{OriginalFormat}".AsSpan(), originalFormat.AsSpan(), 6);
        }

        long timeUnixNano = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        int severityNumber = MapLogLevelToOtlpSeverity(logLevel);
        string severityText = MapLogLevelToSeverityText(logLevel);

        byte[]? pooledIds = null;
        Span<byte> traceId = default; Span<byte> spanId = default;

        if (System.Diagnostics.Activity.Current is not null)
        {
            pooledIds = System.Buffers.ArrayPool<byte>.Shared.Rent(24);
            traceId = pooledIds.AsSpan(0, 16);
            spanId = pooledIds.AsSpan(16, 8);
            System.Diagnostics.Activity.Current.TraceId.CopyTo(traceId);
            System.Diagnostics.Activity.Current.SpanId.CopyTo(spanId);
        }

        try
        {
            ReadOnlySpan<char> body = bodyString.AsSpan();

            string? eventName = eventId.Name;
            int internalSize = OtlpLogRecordFormatter.CalculateLogRecordSize(
                timeUnixNano, severityNumber, severityText, body, fallbackAttributesSize, traceId, spanId, eventName);

            int lengthBytes = 1;
            ulong uSize = (ulong)internalSize;
            while (uSize >= 128) { lengthBytes++; uSize >>= 7; }

            int recordSize = 1 + lengthBytes + internalSize;
            Span<byte> reservedSpan = Batcher.Reserve(recordSize, _scopeBytes, out BufferState bufferState);

            try
            {
                var writer = new ZeroAllocProtobufWriter(reservedSpan);
                writer.WriteTag(2, 2);
                writer.WriteVarInt((ulong)internalSize);

                OtlpLogRecordFormatter.WriteLogRecord(
                    ref writer, timeUnixNano, severityNumber, severityText, body, traceId, spanId, eventName);

                // Write state attributes
                if (stateParams != null)
                {
                    for (int i = 0; i < stateParams.Count; i++)
                    {
                        var kvp = stateParams[i];
                        if (kvp.Key == "{OriginalFormat}") continue;

                        string strVal = kvp.Value?.ToString() ?? string.Empty;
                        int keyBytes = System.Text.Encoding.UTF8.GetByteCount(kvp.Key);
                        int valueBytes = System.Text.Encoding.UTF8.GetByteCount(strVal);

                        int stringValueFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)valueBytes) + valueBytes;
                        int anyValuePayloadSize = stringValueFieldSize;
                        int anyValueFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)anyValuePayloadSize) + anyValuePayloadSize;
                        int keyFieldSize = 1 + ProtobufMath.GetVarIntSize((ulong)keyBytes) + keyBytes;
                        int kvpPayloadSize = keyFieldSize + anyValueFieldSize;

                        // Field 6: attributes (repeated KeyValue)
                        writer.WriteTag(6, 2);
                        writer.WriteVarInt((ulong)kvpPayloadSize);

                        // Field 1 inside KeyValue: key
                        writer.WriteString(1, kvp.Key.AsSpan());

                        // Field 2 inside KeyValue: value (AnyValue)
                        writer.WriteTag(2, 2);
                        writer.WriteVarInt((ulong)anyValuePayloadSize);

                        // Field 1 inside AnyValue: stringValue
                        writer.WriteString(1, strVal.AsSpan());
                    }
                }

                // Enrichment attributes
                OtlpAttributeFormatter.WriteString(ref writer, "logger".AsSpan(), _categoryName.AsSpan(), 6);
                if (!string.IsNullOrEmpty(originalFormat) && !string.Equals(originalFormat, bodyString, StringComparison.Ordinal))
                    OtlpAttributeFormatter.WriteString(ref writer, "{OriginalFormat}".AsSpan(), originalFormat.AsSpan(), 6);
                if (eventId.Id != 0)
                    OtlpAttributeFormatter.WriteInt64(ref writer, "event.id".AsSpan(), eventId.Id, 6);
                if (!string.IsNullOrEmpty(eventId.Name))
                    OtlpAttributeFormatter.WriteString(ref writer, "event.name".AsSpan(), eventId.Name.AsSpan(), 6);
                if (exception is not null)
                {
                    OtlpAttributeFormatter.WriteString(ref writer, "exception.type".AsSpan(), (exception.GetType().FullName ?? exception.GetType().Name).AsSpan(), 6);
                    OtlpAttributeFormatter.WriteString(ref writer, "exception.message".AsSpan(), (exception.Message ?? string.Empty).AsSpan(), 6);
                }

                // Active scopes (StartScope / BeginScope)
                if (scopeSize > 0)
                {
                    fixed (byte* ptr = reservedSpan)
                    {
                        WriteScopeAttributes(ptr, writer.BytesWritten, reservedSpan.Length);
                    }
                }
            }
            finally
            {
                Batcher.Commit(bufferState);
            }
        }
        finally
        {
            if (pooledIds != null) System.Buffers.ArrayPool<byte>.Shared.Return(pooledIds);
        }
    }


    /// <summary>
    /// Accumulates the protobuf size of all active scope attributes (field 6 KeyValues).
    /// </summary>
    private int CalculateScopeAttributesSize()
    {
        var scopeProvider = _provider.ScopeProvider;
        if (scopeProvider is null) return 0;

        var holder = t_sizeHolder ??= new SizeHolder();
        holder.Total = 0;
        scopeProvider.ForEachScope(static (scope, h) =>
        {
            h.Total += MeasureScope(scope);
        }, holder);
        return holder.Total;
    }

    private static int MeasureScope(object? scope)
    {
        if (scope is null) return 0;

        if (scope is IStructuredLogState structured)
        {
            var calc = new OtlpParameterSizeCalculator();
            structured.WriteParameters(ref calc);
            return calc.TotalSize;
        }

        if (scope is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> kvs)
        {
            int size = 0;
            foreach (var kvp in kvs)
            {
                if (kvp.Key == "{OriginalFormat}") continue;
                string strVal = kvp.Value?.ToString() ?? string.Empty;
                size += OtlpAttributeFormatter.CalculateStringSize(kvp.Key.AsSpan(), strVal.AsSpan(), 6);
            }
            return size;
        }

        if (scope is string s)
            return OtlpAttributeFormatter.CalculateStringSize("scope".AsSpan(), s.AsSpan(), 6);

        string fallback = scope.ToString() ?? string.Empty;
        if (fallback.Length == 0) return 0;
        return OtlpAttributeFormatter.CalculateStringSize("scope".AsSpan(), fallback.AsSpan(), 6);
    }

    /// <summary>
    /// Writes all active scope attributes into the buffer starting at <paramref name="offset"/>.
    /// Returns the number of bytes written. Intended to be the last attribute write so the
    /// outer ZeroAllocProtobufWriter does not need its BytesWritten advanced.
    /// </summary>
    private unsafe int WriteScopeAttributes(byte* bufferBase, int offset, int bufferLength)
    {
        var scopeProvider = _provider.ScopeProvider;
        if (scopeProvider is null) return 0;

        var ctx = t_scopeWriteContext ??= new ScopeWriteContext();
        ctx.BufferBase = bufferBase;
        ctx.BufferLength = bufferLength;
        ctx.Offset = offset;
        int start = offset;

        scopeProvider.ForEachScope(static (scope, c) =>
        {
            if (scope is null) return;

            if (scope is IStructuredLogState structured)
            {
                var encoder = new OtlpParameterEncoder(c.BufferBase, c.Offset, c.BufferLength);
                structured.WriteParameters(ref encoder);
                var calc = new OtlpParameterSizeCalculator();
                structured.WriteParameters(ref calc);
                c.Offset += calc.TotalSize;
                return;
            }

            Span<byte> span = new Span<byte>(c.BufferBase + c.Offset, c.BufferLength - c.Offset);
            var w = new ZeroAllocProtobufWriter(span);

            if (scope is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> kvs)
            {
                foreach (var kvp in kvs)
                {
                    if (kvp.Key == "{OriginalFormat}") continue;
                    string strVal = kvp.Value?.ToString() ?? string.Empty;
                    OtlpAttributeFormatter.WriteString(ref w, kvp.Key.AsSpan(), strVal.AsSpan(), 6);
                }
            }
            else if (scope is string s)
            {
                OtlpAttributeFormatter.WriteString(ref w, "scope".AsSpan(), s.AsSpan(), 6);
            }
            else
            {
                string fallback = scope.ToString() ?? string.Empty;
                if (fallback.Length > 0)
                    OtlpAttributeFormatter.WriteString(ref w, "scope".AsSpan(), fallback.AsSpan(), 6);
            }

            c.Offset += w.BytesWritten;
        }, ctx);

        return ctx.Offset - start;
    }

    // Reused per thread: ForEachScope callbacks are synchronous, so one instance per thread suffices.
    [ThreadStatic]
    private static SizeHolder? t_sizeHolder;
    [ThreadStatic]
    private static ScopeWriteContext? t_scopeWriteContext;

    private sealed class SizeHolder
    {
        public int Total;
    }

    private sealed class ScopeWriteContext
    {
        public unsafe byte* BufferBase;
        public int BufferLength;
        public int Offset;
    }

    internal static int MapLogLevelToOtlpSeverity(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => 1,
        LogLevel.Debug => 5,
        LogLevel.Information => 9,
        LogLevel.Warning => 13,
        LogLevel.Error => 17,
        LogLevel.Critical => 21,
        _ => 0
    };

    private static ReadOnlySpan<byte> MapLogLevelToSeverityTextUtf8(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "Trace"u8,
        LogLevel.Debug => "Debug"u8,
        LogLevel.Information => "Information"u8,
        LogLevel.Warning => "Warning"u8,
        LogLevel.Error => "Error"u8,
        LogLevel.Critical => "Fatal"u8,
        _ => "Information"u8
    };

    private static string MapLogLevelToSeverityText(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => "Information"
    };

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();
        public void Dispose() { }
    }
}
