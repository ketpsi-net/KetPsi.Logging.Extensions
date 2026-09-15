using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KetPsi.Extensions.Logging.OpenTelemetry
{
    public static class OpenTelemetryLoggerExtensions
    {
        public static ILoggingBuilder AddZeroAllocOtlp(this ILoggingBuilder builder)
        {
            return builder.AddZeroAllocOtlp(opt => opt);
        }
        public static ILoggingBuilder AddZeroAllocOtlp(this ILoggingBuilder builder, Func<IZeroAllocOtlpOptionsBuilder, IZeroAllocOtlpOptionsBuilder> options)
        {
            // 1. Ensure the logging configuration system is active
            //builder.AddConfiguration();

            // 2. Bind the "Logging:OpenTelemetry" section of appsettings.json to KetPsiOtlpOptions
            Microsoft.Extensions.Logging.Configuration.LoggerProviderOptions.RegisterProviderOptions<KetPsiOtlpOptions, ZeroAllocOtlpLoggerProvider>(builder.Services);

            // 4. Register the Logger Provider itself
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ZeroAllocOtlpLoggerProvider>(sp =>
            {
                // Retrieve the bound configuration
                var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<KetPsiOtlpOptions>>();
                var opt = optionsMonitor.CurrentValue;

                var otlpOptions = new ZeroAllocOtlpOptionsBuilder();
                options?.Invoke(otlpOptions);
                var otlp = otlpOptions.Build();

                // Spin up an isolated HTTP pipeline. 
                // We bypass IHttpClientFactory to avoid circular DI dependencies 
                // and to guarantee this client emits zero internal logs.
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true
                };

                // disposeHandler: true ensures the SocketsHttpHandler is destroyed 
                // when the ZeroAllocOtlpLoggerProvider is disposed on app shutdown.
                var client = new HttpClient(handler, disposeHandler: true)
                {
                    BaseAddress = new Uri(string.IsNullOrEmpty(otlp.Endpoint) ? opt.Endpoint : otlp.Endpoint)
                };

                return new ZeroAllocOtlpLoggerProvider(client, opt.QueueCapacity, otlp.Resources);
            }));

            return builder;
        }
    }
}
