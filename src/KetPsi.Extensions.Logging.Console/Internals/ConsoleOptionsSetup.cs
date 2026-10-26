using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

namespace KetPsi.Extensions.Logging.Console.Internals;

/// <summary>Configuration keys under <c>Logging:Console</c>.</summary>
internal static class ConsoleConfigurationKeys
{
    public const string FormatterName = "FormatterName";
    public const string LogToStandardErrorThreshold = "LogToStandardErrorThreshold";
    public const string FormatterOptions = "FormatterOptions";

    public const string ColorBehavior = "ColorBehavior";
    public const string SingleLine = "SingleLine";
    public const string IncludeScopes = "IncludeScopes";
    public const string TimestampFormat = "TimestampFormat";
    public const string UseUtcTimestamp = "UseUtcTimestamp";
    public const string IncludeCategory = "IncludeCategory";
    public const string IncludeEventId = "IncludeEventId";
}

/// <summary>
/// Reads <see cref="ConsoleLoggerOptions"/> from the provider's section (<c>Logging:Console</c>) explicitly.
/// The reflection-based ConfigurationBinder used before bound nothing under NativeAOT, so FormatterName and
/// LogToStandardErrorThreshold from appsettings.json were ignored.
/// </summary>
internal sealed class ConsoleLoggerOptionsSetup(ILoggerProviderConfiguration<ZeroAllocationConsoleLoggerProvider> providerConfiguration)
    : IConfigureOptions<ConsoleLoggerOptions>
{
    public void Configure(ConsoleLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IConfiguration section = providerConfiguration.Configuration;

        if (section[ConsoleConfigurationKeys.FormatterName] is { Length: > 0 } formatterName)
            options.FormatterName = formatterName;

        if (Enum.TryParse<LogLevel>(section[ConsoleConfigurationKeys.LogToStandardErrorThreshold], ignoreCase: true, out var threshold))
            options.LogToStandardErrorThreshold = threshold;
    }
}

/// <summary>Reads <see cref="ZeroAllocConsoleFormatterOptions"/> from <c>Logging:Console:FormatterOptions</c> explicitly (NativeAOT-safe).</summary>
internal sealed class ZeroAllocConsoleFormatterOptionsSetup(ILoggerProviderConfiguration<ZeroAllocationConsoleLoggerProvider> providerConfiguration)
    : IConfigureOptions<ZeroAllocConsoleFormatterOptions>
{
    public void Configure(ZeroAllocConsoleFormatterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IConfiguration section = providerConfiguration.Configuration.GetSection(ConsoleConfigurationKeys.FormatterOptions);

        if (Enum.TryParse<LoggerColorBehavior>(section[ConsoleConfigurationKeys.ColorBehavior], ignoreCase: true, out var colorBehavior))
            options.ColorBehavior = colorBehavior;

        if (bool.TryParse(section[ConsoleConfigurationKeys.SingleLine], out var singleLine))
            options.SingleLine = singleLine;

        if (bool.TryParse(section[ConsoleConfigurationKeys.IncludeScopes], out var includeScopes))
            options.IncludeScopes = includeScopes;

        // An empty value is meaningful here: it turns the timestamp off.
        if (section[ConsoleConfigurationKeys.TimestampFormat] is { } timestampFormat)
            options.TimestampFormat = timestampFormat;

        if (bool.TryParse(section[ConsoleConfigurationKeys.UseUtcTimestamp], out var useUtcTimestamp))
            options.UseUtcTimestamp = useUtcTimestamp;

        if (bool.TryParse(section[ConsoleConfigurationKeys.IncludeCategory], out var includeCategory))
            options.IncludeCategory = includeCategory;

        if (bool.TryParse(section[ConsoleConfigurationKeys.IncludeEventId], out var includeEventId))
            options.IncludeEventId = includeEventId;
    }
}
