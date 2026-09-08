using System.Buffers;

using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

internal interface IZeroAllocConsoleFormatter
{
    void Format<TState>(
        IBufferWriter<byte> buffer,
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter);
}