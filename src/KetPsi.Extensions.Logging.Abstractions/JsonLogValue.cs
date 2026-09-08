using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace KetPsi.Extensions.Logging.Abstractions;

public static class JsonLogValueSerializationContext
{
    public static JsonSerializerContext? Context { get; set; }
}
public readonly struct JsonLogValue<T>(T? value, JsonTypeInfo<T>? typeInfo = null) : ISpanFormattable, IDeferredUtf8Formatter
{

    private static long s_operationIdSource;

    private readonly T? _value = value;
    private readonly JsonTypeInfo<T>? _typeInfo = typeInfo;
    private readonly long _operationId = Interlocked.Increment(ref s_operationIdSource);
    [ThreadStatic]
    private static WriterState? t_state;

    private sealed class WriterState
    {
        public readonly ArrayBufferWriter<byte> Buffer = new(1024);
        public readonly Utf8JsonWriter Writer = new(Stream.Null); // Dummy init, will be reset
        public long CachedOperationId;
    }

    public void FormatTo(IBufferWriter<byte> writer)
    {
        if (_value is null)
        {
            writer.Write("null"u8);
            return;
        }

        var state = t_state ??= new WriterState();

        // Re-point the JSON writer DIRECTLY to the provided console stream buffer!
        state.Writer.Reset(writer);
        var typeInfo = _typeInfo ?? JsonLogValueSerializationContext.Context?.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        if (typeInfo != null)
        {
            JsonSerializer.Serialize(state.Writer, _value, typeInfo);
        }
        else
        {
            JsonSerializer.Serialize(state.Writer, _value);
        }
    }

    // ---------------------------------------------------------
    // Legacy Paths below (Kept intact for standard sink support)
    // ---------------------------------------------------------

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_value is null)
        {
            const string NullStr = "null";
            if (destination.Length < NullStr.Length)
            {
                charsWritten = 0;
                return false;
            }
            NullStr.AsSpan().CopyTo(destination);
            charsWritten = NullStr.Length;
            return true;
        }

        var state = t_state ??= new WriterState();

        if (state.CachedOperationId != _operationId)
        {
            state.Buffer.Clear();
            state.Writer.Reset(state.Buffer);

            var typeInfo = _typeInfo ?? JsonLogValueSerializationContext.Context?.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
            if (typeInfo != null)
            {
                JsonSerializer.Serialize(state.Writer, _value, typeInfo);
            }
            else
            {
                JsonSerializer.Serialize(state.Writer, _value);
            }

            state.CachedOperationId = _operationId;
        }

        var status = System.Text.Unicode.Utf8.ToUtf16(
            source: state.Buffer.WrittenSpan,
            destination: destination,
            out _,
            out charsWritten,
            replaceInvalidSequences: false,
            isFinalBlock: true);

        return status != System.Buffers.OperationStatus.DestinationTooSmall;
    }

    public override string ToString()
    {
        if (_value is null) return "null";
        var typeInfo = _typeInfo ?? JsonLogValueSerializationContext.Context?.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;

        return typeInfo != null
            ? JsonSerializer.Serialize(_value, typeInfo)
            : JsonSerializer.Serialize(_value);
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
}