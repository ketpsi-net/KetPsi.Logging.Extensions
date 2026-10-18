using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal sealed class OtlpBatcher : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Channel<BatchPayload> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private readonly Task _timerTask;

    private BufferState _active;
    private SpinLock _spinLock;
    private const int BufferCapacity = 65536;

    private int _pendingWriters;

    private readonly byte[] _cachedResourceBytes = [];
    private readonly ConcurrentDictionary<string, byte[]> _scopeBytesCache = new(StringComparer.Ordinal);

    public OtlpBatcher(HttpClient httpClient, int queueCapacity, byte[] resource)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _active = new BufferState(BufferCapacity);
        _spinLock = new SpinLock(enableThreadOwnerTracking: false);
        _pendingWriters = 0;

        _cachedResourceBytes = resource ?? [];

        var channelOptions = new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<BatchPayload>(channelOptions);
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(ProcessQueueAsync);
        _timerTask = Task.Run(TimerLoopAsync);
    }

    /// <summary>
    /// Returns cached ScopeLogs.scope bytes for the logger category.
    /// Zero-alloc after the first call per distinct category.
    /// </summary>
    public byte[] GetOrCreateScopeBytes(string categoryName)
    {
        if (string.IsNullOrEmpty(categoryName))
            categoryName = "KetPsi";

        return _scopeBytesCache.GetOrAdd(categoryName, static name => BuildScopeBytes(name, "1.0.0"));
    }

    private static byte[] BuildScopeBytes(string name, string version)
    {
        int nameBytes = System.Text.Encoding.UTF8.GetByteCount(name);
        int versionBytes = System.Text.Encoding.UTF8.GetByteCount(version);

        int scopePayloadSize =
            (1 + ProtobufMath.GetVarIntSize((ulong)nameBytes) + nameBytes) +
            (1 + ProtobufMath.GetVarIntSize((ulong)versionBytes) + versionBytes);

        int totalSize = 1 + ProtobufMath.GetVarIntSize((ulong)scopePayloadSize) + scopePayloadSize;
        byte[] buffer = new byte[totalSize];
        var writer = new ZeroAllocProtobufWriter(buffer);

        // ScopeLogs.scope = 1 (InstrumentationScope)
        writer.WriteTag(1, 2);
        writer.WriteVarInt((ulong)scopePayloadSize);
        writer.WriteString(1, name.AsSpan());    // InstrumentationScope.name
        writer.WriteString(2, version.AsSpan()); // InstrumentationScope.version

        return buffer;
    }

    private async Task TimerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                Flush();
            }
        }
        catch (OperationCanceledException) { }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> Reserve(int requiredSize, byte[] scopeBytes, out BufferState stateRef)
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);

            // Retire when full or when logger category (scope) changes
            bool scopeChanged = _active.ScopeBytes is not null
                && !ReferenceEquals(_active.ScopeBytes, scopeBytes)
                && _active.BytesWritten > 0;

            if (scopeChanged || _active.BytesWritten + requiredSize > _active.Array.Length)
            {
                _active.IsRetired = true;
                Thread.MemoryBarrier();

                if (Volatile.Read(ref _active.PendingWriters) == 0 && _active.BytesWritten > 0)
                {
                    _channel.Writer.TryWrite(new BatchPayload(_active.Array, _active.BytesWritten, _active.ScopeBytes!));
                }

                _active = new BufferState(BufferCapacity);
            }

            _active.ScopeBytes ??= scopeBytes;

            stateRef = _active;
            Interlocked.Increment(ref stateRef.PendingWriters);

            var slice = stateRef.Array.AsSpan(stateRef.BytesWritten, requiredSize);
            stateRef.BytesWritten += requiredSize;
            return slice;
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(useMemoryBarrier: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(BufferState stateRef)
    {
        int remaining = Interlocked.Decrement(ref stateRef.PendingWriters);

        if (remaining == 0 && stateRef.IsRetired && stateRef.BytesWritten > 0)
        {
            _channel.Writer.TryWrite(new BatchPayload(stateRef.Array, stateRef.BytesWritten, stateRef.ScopeBytes!));
        }
    }

    public void Flush()
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);
            if (_active.BytesWritten == 0) return;

            _active.IsRetired = true;
            Thread.MemoryBarrier();

            if (Volatile.Read(ref _active.PendingWriters) == 0)
            {
                _channel.Writer.TryWrite(new BatchPayload(_active.Array, _active.BytesWritten, _active.ScopeBytes ?? []));
            }

            _active = new BufferState(BufferCapacity);
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(useMemoryBarrier: false);
        }
    }

    internal int CalculateTotalRequestSize(int batchLength, int scopeSize)
    {
        int resourceSize = _cachedResourceBytes.Length;

        int scopeLogsPayload = scopeSize + batchLength;
        int scopeLogsSize = 1 + ProtobufMath.GetVarIntSize((ulong)scopeLogsPayload) + scopeLogsPayload;

        int resourceEnvelope = 1 + ProtobufMath.GetVarIntSize((ulong)resourceSize) + resourceSize;
        int resourceLogsPayload = resourceEnvelope + scopeLogsSize;

        return 1 + ProtobufMath.GetVarIntSize((ulong)resourceLogsPayload) + resourceLogsPayload;
    }

    internal int StitchNetworkPayload(BatchPayload batch, byte[] networkBuffer)
    {
        int resourceSize = _cachedResourceBytes.Length;
        int scopeSize = batch.ScopeBytes.Length;

        int scopeLogsPayloadSize = scopeSize + batch.Length;
        int scopeLogsEnvelopeSize = 1 + ProtobufMath.GetVarIntSize((ulong)scopeLogsPayloadSize) + scopeLogsPayloadSize;

        int resourceEnvelopeSize = 1 + ProtobufMath.GetVarIntSize((ulong)resourceSize) + resourceSize;
        int resourceLogsPayloadSize = resourceEnvelopeSize + scopeLogsEnvelopeSize;

        var writer = new ZeroAllocProtobufWriter(networkBuffer);

        // ExportLogsServiceRequest.resource_logs = 1
        writer.WriteTag(1, 2);
        writer.WriteVarInt((ulong)resourceLogsPayloadSize);

        // ResourceLogs.resource = 1
        writer.WriteTag(1, 2);
        writer.WriteVarInt((ulong)resourceSize);
        writer.WriteRawBytes(_cachedResourceBytes);

        // ResourceLogs.scope_logs = 2
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)scopeLogsPayloadSize);

        // ScopeLogs.scope (InstrumentationScope.name = logger category)
        writer.WriteRawBytes(batch.ScopeBytes);

        // ScopeLogs.log_records
        writer.WriteRawBytes(batch.Buffer.AsSpan(0, batch.Length));

        return writer.BytesWritten;
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    byte[]? networkBuffer = null;

                    try
                    {
                        int totalRequestSize = CalculateTotalRequestSize(batch.Length, batch.ScopeBytes.Length);
                        networkBuffer = ArrayPool<byte>.Shared.Rent(totalRequestSize);
                        int bytesWritten = StitchNetworkPayload(batch, networkBuffer);

                        using var content = new PooledArrayHttpContent(networkBuffer, bytesWritten);
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

                        using var response = await _httpClient.PostAsync(string.Empty, content, token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        if (networkBuffer != null)
                            ArrayPool<byte>.Shared.Return(networkBuffer);

                        ArrayPool<byte>.Shared.Return(batch.Buffer);
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _channel.Writer.Complete();

        try
        {
            await Task.WhenAll(_workerTask, _timerTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        Flush();
        _cts.Dispose();
    }

    internal readonly struct BatchPayload(byte[] buffer, int length, byte[] scopeBytes)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
        public readonly byte[] ScopeBytes = scopeBytes;
    }
}
