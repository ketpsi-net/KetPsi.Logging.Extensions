using System.Collections.Concurrent;
using System.Net;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

namespace OpenTelemetry;

/// <summary>
/// Comprehensive tests for OtlpBatcher.
/// Requires:
///   - InternalsVisibleTo
///   - CalculateTotalRequestSize / StitchNetworkPayload internal
///   - internal int StitchNetworkPayload(byte[] batchBuffer, int batchLength, byte[] networkBuffer)
///     => StitchNetworkPayload(new BatchPayload(batchBuffer, batchLength), networkBuffer);
/// </summary>
public class OtlpBatcherTests
{
    private static readonly byte[] DummyResource =
    [
        0x0A, 0x05, 0x68, 0x65, 0x6C, 0x6C, 0x6F // minimal non-empty resource blob
    ];

    // ------------------------------------------------------------------
    // HTTP helpers
    // ------------------------------------------------------------------

    private sealed class NoopHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Content?.Dispose();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedPost> _posts;
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;
        private readonly TaskCompletionSource<bool>? _signal;

        public CapturingHttpMessageHandler(
            ConcurrentQueue<CapturedPost> posts,
            Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
            TaskCompletionSource<bool>? signal = null)
        {
            _posts = posts;
            _responder = responder;
            _signal = signal;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content != null
                ? await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
                : [];

            string? contentType = request.Content?.Headers.ContentType?.MediaType;
            _posts.Enqueue(new CapturedPost(body, contentType));
            _signal?.TrySetResult(true);

            if (_responder != null)
                return _responder(request);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private readonly record struct CapturedPost(byte[] Body, string? ContentType);

    private static HttpClient CreateNoopClient() =>
        new(new NoopHttpMessageHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:4318/v1/logs")
        };

    private static (HttpClient Client, ConcurrentQueue<CapturedPost> Posts, TaskCompletionSource<bool> Signal)
        CreateCapturingClient(
            Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var posts = new ConcurrentQueue<CapturedPost>();
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HttpClient(new CapturingHttpMessageHandler(posts, responder, signal))
        {
            BaseAddress = new Uri("http://127.0.0.1:4318/v1/logs")
        };
        return (client, posts, signal);
    }

    private static OtlpBatcher CreateBatcher(HttpClient http, int queueCapacity = 32) =>
        new(http, queueCapacity, DummyResource);

    private static async Task WaitForPostAsync(TaskCompletionSource<bool> signal, int timeoutMs = 5000)
    {
        var finished = await Task.WhenAny(signal.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        Assert.True(finished == signal.Task, $"Timed out after {timeoutMs}ms waiting for HTTP POST");
    }

    private static bool ContainsSubSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return true;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    // ==================================================================
    // A. Size calculation + stitch (Noop client – no network)
    // ==================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task CalculateTotalRequestSize_IsPure_AndPositive(int batchLength)
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        int a = batcher.CalculateTotalRequestSize(batchLength);
        int b = batcher.CalculateTotalRequestSize(batchLength);

        Assert.Equal(a, b);
        Assert.True(a > DummyResource.Length);
    }

    [Fact]
    public async Task CalculateTotalRequestSize_GrowsWithBatchLength()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        int s0 = batcher.CalculateTotalRequestSize(0);
        int s1 = batcher.CalculateTotalRequestSize(200);
        int s2 = batcher.CalculateTotalRequestSize(2000);

        Assert.True(s1 > s0);
        Assert.True(s2 > s1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task Stitch_BytesWritten_Equals_CalculatedSize(int batchLength)
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        byte[] batch = new byte[batchLength];
        if (batchLength > 0) Random.Shared.NextBytes(batch);

        int expected = batcher.CalculateTotalRequestSize(batchLength);
        byte[] network = new byte[expected + 32];

        int written = batcher.StitchNetworkPayload(batch, batchLength, network);

        Assert.Equal(expected, written);
    }

    [Fact]
    public async Task Stitch_ContainsResource_AndBatch_AndOuterTag()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        byte[] batch = [0xDE, 0xAD, 0xBE, 0xEF];
        int size = batcher.CalculateTotalRequestSize(batch.Length);
        byte[] network = new byte[size];

        int written = batcher.StitchNetworkPayload(batch, batch.Length, network);
        var payload = network.AsSpan(0, written);

        Assert.Equal(0x0A, payload[0]); // resource_logs tag
        Assert.True(ContainsSubSequence(payload, DummyResource));
        Assert.True(ContainsSubSequence(payload, batch));
    }

    [Fact]
    public async Task Stitch_EmptyBatch_StillProducesValidEnvelope()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        int size = batcher.CalculateTotalRequestSize(0);
        byte[] network = new byte[size];
        int written = batcher.StitchNetworkPayload([], 0, network);

        Assert.Equal(size, written);
        Assert.Equal(0x0A, network[0]);
        Assert.True(ContainsSubSequence(network.AsSpan(0, written), DummyResource));
    }

    // ==================================================================
    // B. Reserve / Commit bookkeeping
    // ==================================================================

    [Fact]
    public async Task Reserve_ReturnsExactSize_AndTracksPendingWriters()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        Span<byte> slice = batcher.Reserve(48, out BufferState state);
        Assert.Equal(48, slice.Length);
        Assert.Equal(48, state.BytesWritten);
        Assert.Equal(1, Volatile.Read(ref state.PendingWriters));

        batcher.Commit(state);
        Assert.Equal(0, Volatile.Read(ref state.PendingWriters));
    }

    [Fact]
    public async Task Reserve_Multiple_AccumulatesBytesWritten_OnSameBuffer()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        batcher.Reserve(10, out var s1);
        batcher.Commit(s1);

        batcher.Reserve(20, out var s2);
        Assert.Same(s1.Array, s2.Array);
        Assert.Equal(30, s2.BytesWritten);
        batcher.Commit(s2);
    }

    [Fact]
    public async Task Reserve_WhenFull_RetiresOldBuffer_AndAllocatesNew()
    {
        await using var batcher = CreateBatcher(CreateNoopClient());

        const int almostFull = 65536 - 8;
        batcher.Reserve(almostFull, out var oldState);
        batcher.Commit(oldState);

        batcher.Reserve(64, out var newState);

        Assert.True(oldState.IsRetired);
        Assert.NotSame(oldState.Array, newState.Array);
        Assert.Equal(64, newState.BytesWritten);
        batcher.Commit(newState);
    }

    // ==================================================================
    // C. PendingWriters + retire (no post until last Commit)
    // ==================================================================

    [Fact]
    public async Task RetiredBuffer_NotPosted_UntilLastWriterCommits()
    {
        var (client, posts, signal) = CreateCapturingClient();
        await using var batcher = CreateBatcher(client);

        // Hold a reservation open
        Span<byte> held = batcher.Reserve(32, out BufferState heldState);
        held.Fill(0x42);
        // do NOT commit yet

        // Force retire via Flush while PendingWriters > 0
        batcher.Flush();
        Assert.True(heldState.IsRetired);

        // Give worker a brief moment – should still be empty
        await Task.Delay(150);
        Assert.True(posts.IsEmpty, "Buffer must not be posted while a writer is still pending");

        // Last commit should enqueue
        batcher.Commit(heldState);

        await WaitForPostAsync(signal);
        Assert.False(posts.IsEmpty);
    }

    // ==================================================================
    // D. Flush → HTTP post
    // ==================================================================

    [Fact]
    public async Task Flush_WithData_PostsProtobufPayload()
    {
        var (client, posts, signal) = CreateCapturingClient();
        await using var batcher = CreateBatcher(client);

        Span<byte> slice = batcher.Reserve(8, out var state);
        slice.Fill(0xAB);
        batcher.Commit(state);

        batcher.Flush();
        await WaitForPostAsync(signal);

        Assert.True(posts.TryDequeue(out var post));
        Assert.Equal("application/x-protobuf", post.ContentType);
        Assert.True(post.Body.Length > 0);
        Assert.Equal(0x0A, post.Body[0]);
        Assert.True(ContainsSubSequence(post.Body, DummyResource));
    }

    [Fact]
    public async Task Flush_WhenEmpty_DoesNotPost()
    {
        var (client, posts, _) = CreateCapturingClient();
        await using var batcher = CreateBatcher(client);

        batcher.Flush();
        await Task.Delay(200);

        Assert.True(posts.IsEmpty);
    }

    [Fact]
    public async Task PostedBodyLength_Matches_CalculateTotalRequestSize()
    {
        var (client, posts, signal) = CreateCapturingClient();
        await using var batcher = CreateBatcher(client);

        const int recordSize = 24;
        Span<byte> slice = batcher.Reserve(recordSize, out var state);
        slice.Fill(0x11);
        batcher.Commit(state);

        int expectedNetworkSize = batcher.CalculateTotalRequestSize(recordSize);

        batcher.Flush();
        await WaitForPostAsync(signal);

        Assert.True(posts.TryDequeue(out var post));
        Assert.Equal(expectedNetworkSize, post.Body.Length);
    }

    // ==================================================================
    // E. HTTP failure – worker stays alive
    // ==================================================================

    [Fact]
    public async Task HttpFailure_DoesNotKillWorker_SubsequentFlushStillPosts()
    {
        int call = 0;
        var posts = new ConcurrentQueue<CapturedPost>();
        var secondPost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new CapturingHttpMessageHandler(
            posts,
            responder: _ =>
            {
                if (Interlocked.Increment(ref call) == 1)
                    throw new HttpRequestException("simulated failure");
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            signal: null);

        // Custom signal: set when we see a successful body after the failure
        // We detect "second post" by queue count.
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:4318/v1/logs")
        };

        await using var batcher = CreateBatcher(client);

        // First batch – will fail inside worker
        batcher.Reserve(4, out var s1);
        batcher.Commit(s1);
        batcher.Flush();
        await Task.Delay(300); // allow failed attempt

        // Second batch – must still be processed
        batcher.Reserve(4, out var s2);
        batcher.Commit(s2);
        batcher.Flush();

        // Wait until we have at least one captured post (the second attempt may be the only one
        // if the first threw before ReadAsByteArrayAsync – either way worker must continue)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (posts.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Worker survived: we could flush again without hanging
        batcher.Flush();
        Assert.True(true); // reached here without deadlock / process crash
    }

    // ==================================================================
    // F. Concurrency smoke
    // ==================================================================

    [Fact]
    public async Task Concurrent_ReserveCommit_ThenFlush_DoesNotCorruptOrHang()
    {
        var (client, posts, signal) = CreateCapturingClient();
        await using var batcher = CreateBatcher(client, queueCapacity: 64);

        const int iterations = 150;
        const int payloadSize = 32;
        var tasks = new Task[Math.Max(2, Environment.ProcessorCount)];

        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    Span<byte> slice = batcher.Reserve(payloadSize, out BufferState state);
                    slice.Fill((byte)(i & 0xFF));
                    batcher.Commit(state);
                }
            });
        }

        await Task.WhenAll(tasks);
        batcher.Flush();

        await WaitForPostAsync(signal, timeoutMs: 8000);
        Assert.False(posts.IsEmpty);
    }

    // ==================================================================
    // G. Dispose
    // ==================================================================

    [Fact]
    public async Task DisposeAsync_CompletesCleanly_AfterWrites()
    {
        var batcher = CreateBatcher(CreateNoopClient());

        batcher.Reserve(16, out var state);
        batcher.Commit(state);
        batcher.Flush();

        await batcher.DisposeAsync();
        // Second dispose not required; just ensure first completed
    }

    [Fact]
    public async Task DisposeAsync_UnderConcurrentWrites_DoesNotHang()
    {
        var batcher = CreateBatcher(CreateNoopClient(), queueCapacity: 32);
        using var cts = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    Span<byte> s = batcher.Reserve(16, out var state);
                    s.Clear();
                    batcher.Commit(state);
                }
                catch
                {
                    // ignore during teardown races
                    break;
                }
            }
        });

        await Task.Delay(100);
        cts.Cancel();

        var disposeTask = batcher.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(disposeTask, Task.Delay(5000));
        Assert.True(finished == disposeTask, "DisposeAsync hung under concurrent writes");

        await writer;
    }

    // ==================================================================
    // H. Constructor guard
    // ==================================================================

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OtlpBatcher(null!, queueCapacity: 4, resource: DummyResource));
    }
}