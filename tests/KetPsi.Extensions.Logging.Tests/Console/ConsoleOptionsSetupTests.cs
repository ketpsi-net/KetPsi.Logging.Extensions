using System.Collections.Generic;

using KetPsi.Extensions.Logging.Console;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Console;

public class ConsoleOptionsSetupTests
{
    // Mirrors a host: the "Logging" section is handed to AddConfiguration, and IConfiguration is NOT registered.
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConfiguration(configuration.GetSection("Logging")).AddZeroAllocConsole());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ReadsEverySettingFromTheConsoleSection()
    {
        using var sp = Build(new()
        {
            ["Logging:Console:FormatterName"] = "json",
            ["Logging:Console:LogToStandardErrorThreshold"] = "warning",
            ["Logging:Console:FormatterOptions:ColorBehavior"] = "Disabled",
            ["Logging:Console:FormatterOptions:SingleLine"] = "true",
            ["Logging:Console:FormatterOptions:IncludeScopes"] = "true",
            ["Logging:Console:FormatterOptions:TimestampFormat"] = "HH:mm:ss ",
            ["Logging:Console:FormatterOptions:UseUtcTimestamp"] = "true",
            ["Logging:Console:FormatterOptions:IncludeCategory"] = "true",
            ["Logging:Console:FormatterOptions:IncludeEventId"] = "true",
        });

        var console = sp.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal("json", console.FormatterName);
        Assert.Equal(LogLevel.Warning, console.LogToStandardErrorThreshold);

        var formatter = sp.GetRequiredService<IOptionsMonitor<ZeroAllocConsoleFormatterOptions>>().CurrentValue;
        Assert.Equal(LoggerColorBehavior.Disabled, formatter.ColorBehavior);
        Assert.True(formatter.SingleLine);
        Assert.True(formatter.IncludeScopes);
        Assert.Equal("HH:mm:ss ", formatter.TimestampFormat);
        Assert.True(formatter.UseUtcTimestamp);
        Assert.True(formatter.IncludeCategory);
        Assert.True(formatter.IncludeEventId);
    }

    [Fact]
    public void KeepsDefaults_WhenValuesAreMissingOrInvalid()
    {
        using var sp = Build(new()
        {
            ["Logging:Console:LogToStandardErrorThreshold"] = "loud",
            ["Logging:Console:FormatterOptions:SingleLine"] = "maybe",
        });

        var console = sp.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal(new ConsoleLoggerOptions().FormatterName, console.FormatterName);
        Assert.Equal(LogLevel.None, console.LogToStandardErrorThreshold);

        var formatter = sp.GetRequiredService<IOptionsMonitor<ZeroAllocConsoleFormatterOptions>>().CurrentValue;
        Assert.False(formatter.SingleLine);
        Assert.False(formatter.IncludeCategory);
    }

    [Fact]
    public void ResolvesTheProvider_WithoutIConfigurationRegistered()
    {
        using var sp = Build(new() { ["Logging:Console:FormatterName"] = "json" });

        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NoIConfiguration");

        Assert.NotNull(logger);
    }
}
