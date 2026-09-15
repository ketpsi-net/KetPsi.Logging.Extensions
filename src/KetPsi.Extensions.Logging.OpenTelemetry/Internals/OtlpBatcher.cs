using System.Buffers;
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

    // --- NEW: Cached Envelopes ---
    private readonly byte[] _cachedResourceBytes = [];
    private readonly byte[] _cachedScopeBytes = [];

    public OtlpBatcher(HttpClient httpClient, int queueCapacity, byte[] resource)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _active = new BufferState(BufferCapacity);
        _spinLock = new SpinLock(enableThreadOwnerTracking: false);
        _pendingWriters = 0;

        _cachedResourceBytes = resource;

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

    private static byte[] BuildCachedScopeBytes(string name, string version)
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
        writer.WriteString(1, name.AsSpan());
        writer.WriteString(2, version.AsSpan());

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
    public Span<byte> Reserve(int requiredSize, out BufferState stateRef)
    {
        bool lockTaken = false;
        try
        {
            _spinLock.Enter(ref lockTaken);

            if (_active.BytesWritten + requiredSize > _active.Array.Length)
            {
                _active.IsRetired = true;
                Thread.MemoryBarrier();

                if (Volatile.Read(ref _active.PendingWriters) == 0 && _active.BytesWritten > 0)
                {
                    _channel.Writer.TryWrite(new BatchPayload(_active.Array, _active.BytesWritten));
                }

                _active = new BufferState(BufferCapacity);
            }

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
            _channel.Writer.TryWrite(new BatchPayload(stateRef.Array, stateRef.BytesWritten));
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
                _channel.Writer.TryWrite(new BatchPayload(_active.Array, _active.BytesWritten));
            }

            _active = new BufferState(BufferCapacity);
        }
        finally
        {
            if (lockTaken) _spinLock.Exit(useMemoryBarrier: false);
        }
    }

    internal int CalculateTotalRequestSize(int batchLength)
    {
        int resourceSize = _cachedResourceBytes.Length;   // raw attributes
        int scopeSize = _cachedScopeBytes.Length;         // already has Tag(1,2)

        int scopeLogsPayload = scopeSize + batchLength;
        int scopeLogsSize = 1 + ProtobufMath.GetVarIntSize((ulong)scopeLogsPayload) + scopeLogsPayload;

        int resourceEnvelope = 1 + ProtobufMath.GetVarIntSize((ulong)resourceSize) + resourceSize;
        int resourceLogsPayload = resourceEnvelope + scopeLogsSize;

        return 1 + ProtobufMath.GetVarIntSize((ulong)resourceLogsPayload) + resourceLogsPayload;
    }
    internal int StitchNetworkPayload(byte[] batchBuffer, int batchLength, byte[] networkBuffer)
    => StitchNetworkPayload(new BatchPayload(batchBuffer, batchLength), networkBuffer);
    internal int StitchNetworkPayload(BatchPayload batch, byte[] networkBuffer)
    {
        int resourceSize = _cachedResourceBytes.Length;
        int scopeSize = _cachedScopeBytes.Length;

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
        writer.WriteRawBytes(_cachedResourceBytes);          // ← MUST be raw attributes

        // ResourceLogs.scope_logs = 2
        writer.WriteTag(2, 2);
        writer.WriteVarInt((ulong)scopeLogsPayloadSize);

        // ScopeLogs.scope (already wrapped)
        writer.WriteRawBytes(_cachedScopeBytes);

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
                        // 1. Calculate OTLP Exact Payload Size
                        int totalRequestSize = CalculateTotalRequestSize(batch.Length);

                        // 2. Rent Buffer & Stitch the Payload synchronously
                        networkBuffer = ArrayPool<byte>.Shared.Rent(totalRequestSize);

                        // By calling a standard method, the ZeroAllocProtobufWriter is destroyed 
                        // before we reach the 'await', satisfying the C# 12 compiler.
                        int bytesWritten = StitchNetworkPayload(batch, networkBuffer);

                        // 3. Send over HTTP
                        using var content = new PooledArrayHttpContent(networkBuffer, bytesWritten);
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

                        using var response = await _httpClient.PostAsync(string.Empty, content, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                    }
                    finally
                    {
                        if (networkBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(networkBuffer);
                        }

                        ArrayPool<byte>.Shared.Return(batch.Buffer);
                    }
                }
            }
        }
        catch (Exception ex)
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

    internal readonly struct BatchPayload(byte[] buffer, int length)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
    }
}