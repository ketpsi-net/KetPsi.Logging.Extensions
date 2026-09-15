using Microsoft.Extensions.Logging;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

// CRITICAL: This exact alias tells the .NET ILoggerFactory to read the 
// "Logging:OpenTelemetry:LogLevel" section from appsettings.json.
[ProviderAlias("OpenTelemetry")]
internal sealed class ZeroAllocOtlpLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    private readonly OtlpBatcher _batcher;
    private IExternalScopeProvider? _scopeProvider;

    public OtlpBatcher Batcher => _batcher;

    public ZeroAllocOtlpLoggerProvider(HttpClient httpClient, int queueCapacity, byte[] resources)
    {
        _batcher = new OtlpBatcher(httpClient, queueCapacity, resources);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ZeroAllocOtlpLogger(categoryName, this);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    internal IExternalScopeProvider? ScopeProvider => _scopeProvider;

    public void Dispose()
    {
        _batcher.Flush();
        _ = _batcher.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync().ConfigureAwait(false);
    }
}