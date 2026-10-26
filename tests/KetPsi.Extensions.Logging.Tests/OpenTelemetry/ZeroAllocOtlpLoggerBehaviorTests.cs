using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.Logging;

namespace OpenTelemetry;

public class ZeroAllocOtlpLoggerBehaviorTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly List<byte[]> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

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

    private static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static async Task<byte[]> ExportOne(bool includeFormattedMessage)
    {
        var handler = new CapturingHandler();
        var provider = new ZeroAllocOtlpLoggerProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, 16, [], includeFormattedMessage);
        var logger = provider.CreateLogger("Behavior");
        int orderId = 98765;

        logger.LogInformation($"Processed order {orderId:@OrderId}");
        await provider.DisposeAsync(); // flushes and drains

        return Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task IncludeFormattedMessage_True_SendsRenderedBody_AndTemplateAttribute()
    {
        byte[] payload = await ExportOne(includeFormattedMessage: true);

        Assert.True(Contains(payload, "Processed order 98765"));
        Assert.True(Contains(payload, "{OriginalFormat}"));
        Assert.True(Contains(payload, "Processed order {OrderId}"));
    }

    [Fact]
    public async Task IncludeFormattedMessage_False_SendsTemplateBody_WithoutDuplicateAttribute()
    {
        byte[] payload = await ExportOne(includeFormattedMessage: false);

        Assert.False(Contains(payload, "Processed order 98765"));
        Assert.True(Contains(payload, "Processed order {OrderId}"));
        Assert.False(Contains(payload, "{OriginalFormat}"));
        Assert.True(Contains(payload, "OrderId")); // the value still travels as an attribute
    }

    private static byte[] Attribute(string name, string value)
    {
        var buffer = new byte[1024];
        var writer = new ZeroAllocProtobufWriter(buffer);
        OtlpAttributeFormatter.WriteString(ref writer, Encoding.UTF8.GetBytes(name), Encoding.UTF8.GetBytes(value), 6);
        return buffer.AsSpan(0, writer.BytesWritten).ToArray();
    }

    private sealed record Payload(int A, string B);

    [Theory]
    [InlineData(true)]  // attribute values reused from the rendered body
    [InlineData(false)] // template body: every value formatted for its attribute
    public async Task AttributeValues_AreIdentical_WhetherOrNotReusedFromTheBody(bool includeFormattedMessage)
    {
        var handler = new CapturingHandler();
        var provider = new ZeroAllocOtlpLoggerProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, 16, [], includeFormattedMessage);
        var logger = provider.CreateLogger("Reuse");

        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        decimal amount = 1500.75m;
        string user = "alice";
        var payload = new Payload(1, "x");

        logger.LogInformation($"id {id:@Id} amount {amount:@Amount} fmt {amount:@Fmt:N2} user {user:@User} aligned [{user,8:@Aligned}] masked {id:@Masked:mask:4..8} json {payload:@Json:json}");
        await provider.DisposeAsync();
        byte[] body = Assert.Single(handler.Bodies);

        Assert.True(Contains(body, Attribute("Id", id.ToString())));
        Assert.True(Contains(body, Attribute("Amount", amount.ToString())));
        // A format specifier applies to the message only; the attribute keeps the raw value.
        Assert.True(Contains(body, Attribute("Fmt", amount.ToString())));
        Assert.True(Contains(body, Attribute("User", "alice")));
        // Alignment padding never leaks into the attribute.
        Assert.True(Contains(body, Attribute("Aligned", "alice")));
        Assert.True(Contains(body, Attribute("Masked", "1234****-1234-1234-1234-123456789abc")));
        Assert.True(Contains(body, Attribute("Json", "{\"A\":1,\"B\":\"x\"}")));

        if (includeFormattedMessage)
            Assert.True(Contains(body, $"fmt {amount:N2} user alice aligned [   alice]"));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task SaturatedQueue_DropsRecordsBeforeEncoding()
    {
        var handler = new BlockingHandler();
        var provider = new ZeroAllocOtlpLoggerProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/v1/logs") }, queueCapacity: 1, [], includeFormattedMessage: true);
        var logger = provider.CreateLogger("Saturation");
        var batcher = provider.Batcher;
        string padding = new('x', 4_000);
        try
        {
            // 1. Produce until the worker holds a batch in flight (it blocks in the handler from then on).
            //    Waiting for this matters: if the worker starts late, it would dequeue mid-test and clear saturation.
            int i = 0;
            while (Volatile.Read(ref handler.Requests) == 0 && i < 100_000)
            {
                logger.LogInformation($"{padding:@Padding} {i:@I}");
                if (batcher.IsSaturated) Thread.Sleep(1); // queue full: give the worker time to pick up its batch
                i++;
            }
            Assert.Equal(1, Volatile.Read(ref handler.Requests));

            // 2. Fill the queue behind it; with the worker blocked, the queue now stays full.
            for (int j = 0; j < 2_000; j++)
                logger.LogInformation($"{padding:@Padding} {j:@I}");

            Assert.True(batcher.IsSaturated);
            Assert.True(batcher.DroppedRecords > 0);

            long droppedBefore = batcher.DroppedRecords;
            logger.LogInformation($"dropped {1:@One}");
            Assert.Equal(droppedBefore + 1, batcher.DroppedRecords);
        }
        finally
        {
            handler.Release.TrySetResult();
            await provider.DisposeAsync();
        }
    }
}
