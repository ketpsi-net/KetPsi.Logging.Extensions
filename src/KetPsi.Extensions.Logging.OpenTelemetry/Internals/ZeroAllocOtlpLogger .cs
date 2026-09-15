using System.Buffers;
using System.Diagnostics;

using KetPsi.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal sealed class ZeroAllocOtlpLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ZeroAllocOtlpLoggerProvider _provider;

    public OtlpBatcher Batcher => _provider.Batcher;
    public string CategoryName => _categoryName;

    public ZeroAllocOtlpLogger(string categoryName, ZeroAllocOtlpLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
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

        if (StructuredLogProcessorCache<TState>.Process != null)
        {
            StructuredLogProcessorCache<TState>.Process(this, logLevel, ref state, exception);
            return;
        }

        ProcessFallback(logLevel, state, exception, formatter);
    }

    internal unsafe void ProcessStructured<TStructuredState>(
        LogLevel logLevel,
        ref TStructuredState structuredState,
        Exception? exception)
        where TStructuredState : IStructuredLogState
    {
        long timeUnixNano = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        int severityNumber = MapLogLevelToOtlpSeverity(logLevel);
        string severityText = MapLogLevelToSeverityText(logLevel);

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

        try
        {
            var sizeCalculator = new OtlpParameterSizeCalculator();
            structuredState.WriteParameters(ref sizeCalculator);

            ReadOnlySpan<char> body = structuredState.OriginalFormat;

            int internalSize = OtlpLogRecordFormatter.CalculateLogRecordSize(
                timeUnixNano, severityNumber, severityText, body, sizeCalculator.TotalSize, traceId, spanId);

            int lengthBytes = 1;
            ulong uSize = (ulong)internalSize;
            while (uSize >= 128)
            {
                lengthBytes++;
                uSize >>= 7;
            }
            int recordSize = 1 + lengthBytes + internalSize;

            Span<byte> reservedSpan = Batcher.Reserve(recordSize, out BufferState bufferState);

            try
            {
                var writer = new ZeroAllocProtobufWriter(reservedSpan);

                writer.WriteTag(2, 2);
                writer.WriteVarInt((ulong)internalSize);

                OtlpLogRecordFormatter.WriteLogRecord(
                    ref writer, timeUnixNano, severityNumber, severityText, body, traceId, spanId);

                if (sizeCalculator.TotalSize > 0)
                {
                    fixed (byte* ptr = reservedSpan)
                    {
                        var encoder = new OtlpParameterEncoder(ptr, writer.BytesWritten, reservedSpan.Length);
                        structuredState.WriteParameters(ref encoder);
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
            if (pooledIds != null)
            {
                ArrayPool<byte>.Shared.Return(pooledIds);
            }
        }
    }

    private unsafe void ProcessFallback<TState>(
        LogLevel logLevel,
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

        string bodyString = originalFormat ?? formatter(state, exception);
        if (string.IsNullOrEmpty(bodyString) && exception is null) return;

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

            int internalSize = OtlpLogRecordFormatter.CalculateLogRecordSize(
                timeUnixNano, severityNumber, severityText, body, fallbackAttributesSize, traceId, spanId);

            int lengthBytes = 1;
            ulong uSize = (ulong)internalSize;
            while (uSize >= 128) { lengthBytes++; uSize >>= 7; }

            int recordSize = 1 + lengthBytes + internalSize;
            Span<byte> reservedSpan = Batcher.Reserve(recordSize, out BufferState bufferState);

            try
            {
                var writer = new ZeroAllocProtobufWriter(reservedSpan);
                writer.WriteTag(2, 2);
                writer.WriteVarInt((ulong)internalSize);

                OtlpLogRecordFormatter.WriteLogRecord(
                    ref writer, timeUnixNano, severityNumber, severityText, body, traceId, spanId);

                // Write Attributes
                if (stateParams != null && fallbackAttributesSize > 0)
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