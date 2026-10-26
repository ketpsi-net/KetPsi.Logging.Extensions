using System.Collections.Generic;

using KetPsi.Extensions.Logging.OpenTelemetry;
using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenTelemetry;

public class KetPsiOtlpOptionsSetupTests
{
    private static KetPsiOtlpOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConfiguration(configuration.GetSection("Logging")).AddZeroAllocOtlp());
        return services.BuildServiceProvider().GetRequiredService<IOptions<KetPsiOtlpOptions>>().Value;
    }

    [Fact]
    public void ReadsEverySettingFromTheOpenTelemetrySection()
    {
        var options = Resolve(new()
        {
            ["Logging:OpenTelemetry:Endpoint"] = "http://collector:4318/v1/logs",
            ["Logging:OpenTelemetry:QueueCapacity"] = "64",
            ["Logging:OpenTelemetry:IncludeFormattedMessage"] = "false",
            ["Logging:OpenTelemetry:Resources:service.name"] = "orders-api",
            ["Logging:OpenTelemetry:Resources:deployment.environment"] = "prod",
        });

        Assert.Equal("http://collector:4318/v1/logs", options.Endpoint);
        Assert.Equal(64, options.QueueCapacity);
        Assert.False(options.IncludeFormattedMessage);
        Assert.Equal("orders-api", options.Resources["service.name"]);
        Assert.Equal("prod", options.Resources["deployment.environment"]);
    }

    [Fact]
    public void KeepsDefaults_WhenSectionIsMissingOrInvalid()
    {
        var options = Resolve(new()
        {
            ["Logging:OpenTelemetry:QueueCapacity"] = "not-a-number",
            ["Logging:OpenTelemetry:IncludeFormattedMessage"] = "maybe",
        });

        Assert.Equal("http://localhost:4318/v1/logs", options.Endpoint);
        Assert.Equal(1024, options.QueueCapacity);
        Assert.True(options.IncludeFormattedMessage);
        Assert.Empty(options.Resources);
    }
}
