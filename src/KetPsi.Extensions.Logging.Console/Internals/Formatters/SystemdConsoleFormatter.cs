using System.Buffers;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.Console.Internals.Formatters;

internal sealed class SystemdConsoleFormatter : IZeroAllocConsoleFormatter
{
    public void Format<TState>(
        IBufferWriter<byte> buffer, string categoryName, LogLevel logLevel, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var priority = logLevel switch
        {
            LogLevel.Critical => "<2>"u8,
            LogLevel.Error => "<3>"u8,
            LogLevel.Warning => "<4>"u8,
            LogLevel.Information => "<6>"u8,
            LogLevel.Debug or LogLevel.Trace => "<7>"u8,
            _ => "<6>"u8
        };

        buffer.Write(priority);

        int maxCatBytes = System.Text.Encoding.UTF8.GetMaxByteCount(categoryName.Length);
        int catWritten = System.Text.Encoding.UTF8.GetBytes(categoryName, buffer.GetSpan(maxCatBytes));
        buffer.Advance(catWritten);

        buffer.Write(": "u8);

        if (state is IDeferredUtf8Formatter deferredFormatter) deferredFormatter.FormatTo(buffer);
        else
        {
            string msg = formatter(state, exception);
            int maxMsgBytes = System.Text.Encoding.UTF8.GetMaxByteCount(msg.Length);
            int msgWritten = System.Text.Encoding.UTF8.GetBytes(msg, buffer.GetSpan(maxMsgBytes));
            buffer.Advance(msgWritten);
        }

        buffer.Write("\n"u8);
    }
}
