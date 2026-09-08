using System.Buffers;
using System.Text.Json;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.Console.Internals.Formatters;

internal sealed class JsonConsoleFormatter : IZeroAllocConsoleFormatter
{
    [ThreadStatic]
    private static Utf8JsonWriter? _tsJsonWriter;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _tsMessageBuffer;

    public void Format<TState>(
        IBufferWriter<byte> buffer, string categoryName, LogLevel logLevel, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var jsonWriter = _tsJsonWriter ??= new Utf8JsonWriter(Stream.Null);
        jsonWriter.Reset(buffer);

        jsonWriter.WriteStartObject();

        Span<char> timeSpan = stackalloc char[24];
        if (DateTime.UtcNow.TryFormat(timeSpan, out int timeLen, "O"))
        {
            Span<byte> utf8Time = stackalloc byte[32];
            int timeBytes = System.Text.Encoding.UTF8.GetBytes(timeSpan[..timeLen], utf8Time);
            jsonWriter.WriteString("Timestamp"u8, utf8Time[..timeBytes]);
        }

        jsonWriter.WriteString("LogLevel"u8, GetLogLevelString(logLevel));
        jsonWriter.WriteString("Category"u8, categoryName);

        var msgBuffer = _tsMessageBuffer ??= new ArrayBufferWriter<byte>(512);
        msgBuffer.Clear();

        if (state is IDeferredUtf8Formatter deferredFormatter) deferredFormatter.FormatTo(msgBuffer);
        else
        {
            string msg = formatter(state, exception);
            int maxBytes = System.Text.Encoding.UTF8.GetMaxByteCount(msg.Length);
            int written = System.Text.Encoding.UTF8.GetBytes(msg, msgBuffer.GetSpan(maxBytes));
            msgBuffer.Advance(written);
        }

        jsonWriter.WriteString("Message"u8, msgBuffer.WrittenSpan);

        if (ScopeContext.Current != null)
        {
            jsonWriter.WriteStartArray("Scopes"u8);
            WriteScopes(jsonWriter, ScopeContext.Current);
            jsonWriter.WriteEndArray();
        }

        if (exception != null) jsonWriter.WriteString("Exception"u8, exception.ToString());

        jsonWriter.WriteEndObject();
        jsonWriter.Flush();

        buffer.Write("\n"u8);
    }

    private static void WriteScopes(Utf8JsonWriter jsonWriter, ScopeNode node)
    {
        if (node.Parent != null) WriteScopes(jsonWriter, node.Parent);
        jsonWriter.WriteStringValue(node.WrittenSpan);
    }

    private static ReadOnlySpan<byte> GetLogLevelString(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace"u8,
        LogLevel.Debug => "Debug"u8,
        LogLevel.Information => "Information"u8,
        LogLevel.Warning => "Warning"u8,
        LogLevel.Error => "Error"u8,
        LogLevel.Critical => "Critical"u8,
        _ => "None"u8
    };
}
