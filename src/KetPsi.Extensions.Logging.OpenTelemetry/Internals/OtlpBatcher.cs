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
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Recycled batch buffers. Once warm, the logging threads never allocate a buffer: the HTTP worker
    // hands each sent (or dropped) buffer back here instead of releasing it.
    private readonly object _freeLock = new();
    private readonly Stack<BufferState> _freeBuffers;
    private readonly int _maxFreeBuffers;

    // Single-reader worker: one reusable request buffer instead of renting ~64 KB per POST.
    private byte[] _networkBuffer = [];

    private long _exportedBatches;
    private long _droppedBatches;

    private readonly byte[] _cachedResourceBytes = [];
    private readonly ConcurrentDictionary<string, byte[]> _scopeBytesCache = new(StringComparer.Ordinal);

    /// <summary>Batches handed to the HTTP worker (diagnostics).</summary>
    internal long ExportedBatches => Interlocked.Read(ref _exportedBatches);

    /// <summary>Batches dropped because the export queue was full (diagnostics).</summary>
    internal long DroppedBatches => Interlocked.Read(ref _droppedBatches);

    public OtlpBatcher(HttpClient httpClient, int queueCapacity, byte[] resource)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _spinLock = new SpinLock(enableThreadOwnerTracking: false);

        // Every buffer is either active, queued, being sent, or free; this bounds the free list.
        _maxFreeBuffers = queueCapacity + 4;
        _freeBuffers = new Stack<BufferState>(Math.Min(_maxFreeBuffers, 64));
        _active = RentBuffer(BufferCapacity);

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
                RetireActive();
                // A record larger than a standard buffer gets a dedicated (non-pooled) one.
                _active = RentBuffer(Math.Max(BufferCapacity, requiredSize));
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

        if (remaining == 0 && stateRef.IsRetired)
        {
            Dispatch(stateRef);
        }
    }

    public void Flush()
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);
            if (_active.BytesWritten == 0) return;

            RetireActive();
            _active = RentBuffer(BufferCapacity);
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(useMemoryBarrier: false);
        }
    }

    /// <summary>Retires the active buffer (caller holds the spin lock); it is dispatched once its last writer commits.</summary>
    private void RetireActive()
    {
        var retired = _active;
        retired.IsRetired = true;
        Thread.MemoryBarrier();

        if (Volatile.Read(ref retired.PendingWriters) == 0)
        {
            Dispatch(retired);
        }
    }

    /// <summary>
    /// Queues a retired buffer for export exactly once. Both the retiring thread and the last committing writer
    /// can observe "retired with no pending writers", so the claim is atomic.
    /// </summary>
    private void Dispatch(BufferState state)
    {
        if (Interlocked.Exchange(ref state.Dispatched, 1) != 0) return;

        if (state.BytesWritten == 0)
        {
            ReturnBuffer(state);
            return;
        }

        if (_channel.Writer.TryWrite(new BatchPayload(state.Array, state.BytesWritten, state.ScopeBytes ?? [], state)))
        {
            Interlocked.Increment(ref _exportedBatches);
        }
        else
        {
            // Export queue full (collector slower than the producers): drop the batch but keep the buffer.
            Interlocked.Increment(ref _droppedBatches);
            ReturnBuffer(state);
        }
    }

    private BufferState RentBuffer(int minimumCapacity)
    {
        if (minimumCapacity <= BufferCapacity)
        {
            lock (_freeLock)
            {
                if (_freeBuffers.TryPop(out var recycled))
                {
                    recycled.Reset();
                    return recycled;
                }
            }
            return new BufferState(BufferCapacity, pooled: true);
        }
        return new BufferState(minimumCapacity, pooled: false);
    }

    internal void ReturnBuffer(BufferState state)
    {
        if (state.Pooled)
        {
            lock (_freeLock)
            {
                if (_freeBuffers.Count < _maxFreeBuffers)
                {
                    _freeBuffers.Push(state);
                    return;
                }
            }
        }
        state.Release();
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

        try
        {
            // Not cancelled by the token: on dispose the channel is completed and the queue drained.
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    try
                    {
                        int totalRequestSize = CalculateTotalRequestSize(batch.Length, batch.ScopeBytes.Length);
                        if (_networkBuffer.Length < totalRequestSize)
                            _networkBuffer = new byte[Math.Max(totalRequestSize, BufferCapacity * 2)];
                        int bytesWritten = StitchNetworkPayload(batch, _networkBuffer);

                        using var content = new PooledArrayHttpContent(_networkBuffer, bytesWritten);
                        using var response = await _httpClient.PostAsync(string.Empty, content, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        if (batch.Owner != null) ReturnBuffer(batch.Owner);
                        else ArrayPool<byte>.Shared.Return(batch.Buffer);
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
        // Send what is buffered, then let the worker drain the queue before stopping.
        Flush();
        _channel.Writer.TryComplete();
        // Bound the drain: an unreachable collector must not stall shutdown on HTTP timeouts.
        _cts.CancelAfter(DrainTimeout);

        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await _cts.CancelAsync();
        try
        {
            await _timerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }

    internal readonly struct BatchPayload(byte[] buffer, int length, byte[] scopeBytes, BufferState? owner = null)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
        public readonly byte[] ScopeBytes = scopeBytes;

        /// <summary>The recycled buffer this batch lives in; returned to the batcher after sending.</summary>
        public readonly BufferState? Owner = owner;
    }
}
