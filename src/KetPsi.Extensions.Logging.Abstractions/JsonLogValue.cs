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

        // The same value is rendered for the message body and again for sink attributes;
        // serialize once per log and copy the cached UTF-8 afterwards.
        writer.Write(GetUtf8());
    }

    private ReadOnlySpan<byte> GetUtf8()
    {
        var state = t_state ??= new WriterState();
        if (state.CachedOperationId != _operationId)
        {
            state.Buffer.Clear();
            state.Writer.Reset(state.Buffer);
            Serialize(state.Writer);
            state.CachedOperationId = _operationId;
        }
        return state.Buffer.WrittenSpan;
    }

    private void Serialize(Utf8JsonWriter writer)
    {
        if (_typeInfo != null)
        {
            JsonSerializer.Serialize(writer, _value!, _typeInfo);
            return;
        }

        // Generated code declares values whose type it cannot name as object; resolve the runtime type
        // so a source-generated context (required under NativeAOT) can still be used.
        Type type = typeof(T) == typeof(object) ? _value!.GetType() : typeof(T);
        JsonTypeInfo? info = JsonLogValueSerializationContext.Context?.GetTypeInfo(type);

        if (info is JsonTypeInfo<T> typed) JsonSerializer.Serialize(writer, _value!, typed);
        else if (info != null) JsonSerializer.Serialize(writer, (object?)_value, info);
        else JsonSerializer.Serialize(writer, _value);
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

        var status = System.Text.Unicode.Utf8.ToUtf16(
            source: GetUtf8(),
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
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) Serialize(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
}