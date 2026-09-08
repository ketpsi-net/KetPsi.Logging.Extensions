using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

[ProviderAlias("Console")]
internal sealed class ZeroAllocationConsoleLoggerProvider(
    IZeroAllocConsoleFormatter formatter,
    Stream? stdout = null,
    Stream? stderr = null,
    IOptionsMonitor<ConsoleLoggerOptions>? consoleOptions = null) : ILoggerProvider
{
    private readonly ConsoleLogProcessor _processor = new(stdout, stderr);
    private readonly IZeroAllocConsoleFormatter _formatter = formatter;
    private readonly ConcurrentDictionary<string, ZeroAllocationConsoleLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<ConsoleLoggerOptions>? _consoleOptions = consoleOptions;

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name =>
            new ZeroAllocationConsoleLogger(name, _processor, _formatter, _consoleOptions));
    }

    public void Dispose()
    {
        _loggers.Clear();

        // This blocks briefly to flush remaining logs to stdout/stderr before the app exits
        _processor.Dispose();
    }
}
