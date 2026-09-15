using KetPsi.Extensions.Logging.Console;
using KetPsi.Extensions.Logging.Console.Internals;
using KetPsi.Extensions.Logging.Console.Internals.Formatters;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.Console;

public static partial class ConsoleLoggerExtensions
{
    public static ILoggingBuilder AddZeroAllocConsole(this ILoggingBuilder config)
    {
        return AddZeroAllocConsole(config, c => c);
    }
    public static ILoggingBuilder AddZeroAllocConsole(this ILoggingBuilder config, Func<IZeroAllocConsoleLoggerConfig, IZeroAllocConsoleLoggerConfig> configure)
    {
        var services = config.Services;

        configure(new ZeroAllocConsoleLoggerConfig());
        services.AddOptions<ConsoleLoggerOptions>().BindConfiguration("Logging:Console");
        services.AddOptions<SimpleConsoleFormatterOptions>().BindConfiguration("Logging:Console:FormatterOptions");
        services.AddOptions<ZeroAllocConsoleFormatterOptions>().BindConfiguration("Logging:Console:FormatterOptions");

        // 1. Register the formatters
        services.TryAddSingleton<SimpleConsoleFormatter>();
        services.TryAddSingleton<JsonConsoleFormatter>();
        services.TryAddSingleton<SystemdConsoleFormatter>();

        Microsoft.Extensions.Logging.Configuration.LoggerProviderOptions.RegisterProviderOptions<ConsoleLoggerOptions, ZeroAllocationConsoleLoggerProvider>(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ZeroAllocationConsoleLoggerProvider>(sp =>
        {
            var consoleOptions = sp.GetService<IOptionsMonitor<ConsoleLoggerOptions>>();

            string formatterName = consoleOptions?.CurrentValue.FormatterName ?? ConsoleFormatterNames.Simple;

            IZeroAllocConsoleFormatter formatter = formatterName switch
            {
                ConsoleFormatterNames.Json => sp.GetRequiredService<JsonConsoleFormatter>(),
                ConsoleFormatterNames.Systemd => sp.GetRequiredService<SystemdConsoleFormatter>(),
                _ => sp.GetRequiredService<SimpleConsoleFormatter>()
            };

            return new ZeroAllocationConsoleLoggerProvider(formatter, null, null, consoleOptions);
        }));
        return config;
    }
}
