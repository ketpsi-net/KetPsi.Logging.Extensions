using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

namespace OpenTelemetry;

public class OtlpBatcherRecyclingTests
{
    private static readonly byte[] Scope = [0x0A, 0x00];

    /// <summary>Holds every request until released, simulating a collector slower than the producers.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void WriteRecords(OtlpBatcher batcher, int count, int size)
    {
        for (int i = 0; i < count; i++)
        {
            Span<byte> slice = batcher.Reserve(size, Scope, out BufferState state);
            slice.Fill(1);
            batcher.Commit(state);
        }
    }

    [Fact]
    public async Task FullQueue_DropsBatches_AndRecyclesTheirBuffers_WithoutAllocating()
    {
        var handler = new BlockingHandler();
        var batcher = new OtlpBatcher(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, queueCapacity: 2, resource: []);
        try
        {
            // ~16 records per 64 KB buffer: fill the queue and the in-flight request, then keep producing.
            WriteRecords(batcher, 2_000, 4_000);
            Assert.True(batcher.DroppedBatches > 0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            WriteRecords(batcher, 2_000, 4_000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // Dropped buffers go back to the free list, so producing more batches reuses them.
            Assert.Equal(0, allocated);
        }
        finally
        {
            handler.Release.TrySetResult();
            await batcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task SentBuffers_AreReused_AfterTheWorkerFinishes()
    {
        var handler = new BlockingHandler();
        handler.Release.TrySetResult(); // collector answers immediately
        var batcher = new OtlpBatcher(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, queueCapacity: 64, resource: []);
        try
        {
            // Synchronous waits keep the measurement on one thread (await could resume elsewhere).
            WriteRecords(batcher, 200, 4_000); // warm the free list
            Thread.Sleep(200);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 5; round++)
            {
                WriteRecords(batcher, 32, 4_000); // two buffers' worth
                Thread.Sleep(50);                 // let the worker send and return them
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(batcher.ExportedBatches > 0);
            Assert.Equal(0, allocated);
        }
        finally
        {
            await batcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_SendsBufferedRecords()
    {
        var handler = new BlockingHandler();
        handler.Release.TrySetResult();
        var batcher = new OtlpBatcher(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, queueCapacity: 4, resource: []);

        WriteRecords(batcher, 3, 100); // far below one buffer: only a flush sends it
        await batcher.DisposeAsync();

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RecordLargerThanABuffer_GetsADedicatedBuffer()
    {
        var handler = new BlockingHandler();
        handler.Release.TrySetResult();
        var batcher = new OtlpBatcher(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, queueCapacity: 4, resource: []);
        try
        {
            Span<byte> slice = batcher.Reserve(100_000, Scope, out BufferState state);
            Assert.Equal(100_000, slice.Length);
            batcher.Commit(state);
        }
        finally
        {
            await batcher.DisposeAsync();
        }

        Assert.Equal(1, handler.Requests);
    }
}
