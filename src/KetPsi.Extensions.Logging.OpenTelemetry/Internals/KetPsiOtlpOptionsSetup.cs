using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

/// <summary>Configuration keys under <c>Logging:OpenTelemetry</c>.</summary>
internal static class KetPsiOtlpConfigurationKeys
{
    public const string Endpoint = "Endpoint";
    public const string QueueCapacity = "QueueCapacity";
    public const string IncludeFormattedMessage = "IncludeFormattedMessage";
    public const string Resources = "Resources";
}

/// <summary>
/// Reads <see cref="KetPsiOtlpOptions"/> from the provider's configuration section (<c>Logging:OpenTelemetry</c>,
/// or the provider's full type name) explicitly. The reflection-based ConfigurationBinder used before silently
/// bound nothing under NativeAOT, so an endpoint set in appsettings.json was ignored and logs went to the default.
/// </summary>
internal sealed class KetPsiOtlpOptionsSetup(ILoggerProviderConfiguration<ZeroAllocOtlpLoggerProvider> providerConfiguration)
    : IConfigureOptions<KetPsiOtlpOptions>
{
    public void Configure(KetPsiOtlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IConfiguration section = providerConfiguration.Configuration;

        if (section[KetPsiOtlpConfigurationKeys.Endpoint] is { Length: > 0 } endpoint)
            options.Endpoint = endpoint;

        if (int.TryParse(section[KetPsiOtlpConfigurationKeys.QueueCapacity], NumberStyles.Integer, CultureInfo.InvariantCulture, out var queueCapacity)
            && queueCapacity > 0)
            options.QueueCapacity = queueCapacity;

        if (bool.TryParse(section[KetPsiOtlpConfigurationKeys.IncludeFormattedMessage], out var includeFormattedMessage))
            options.IncludeFormattedMessage = includeFormattedMessage;

        foreach (var resource in section.GetSection(KetPsiOtlpConfigurationKeys.Resources).GetChildren())
        {
            if (resource.Value is { } value)
                options.Resources[resource.Key] = value;
        }
    }
}
