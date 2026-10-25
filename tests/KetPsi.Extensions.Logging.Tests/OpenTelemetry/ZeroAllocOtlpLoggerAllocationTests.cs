using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.Logging;

namespace OpenTelemetry;

public class ZeroAllocOtlpLoggerAllocationTests
{
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static long AllocatedPer1000(Action log)
    {
        for (int i = 0; i < 2000; i++) log();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) log();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void InterceptedCalls_ThroughLoggerFactory_DoNotAllocatePerLog()
    {
        using var provider = new ZeroAllocOtlpLoggerProvider(
            new HttpClient(new NoOpHandler()) { BaseAddress = new Uri("http://localhost/v1/logs") }, 1024, []);
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        var logger = factory.CreateLogger("Alloc");

        int count = 5;
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        decimal amount = 1.5m;
        string user = "u";

        long allocated;
        using (logger.StartScope($"Tenant {user:@Tenant}"))
        {
            allocated = AllocatedPer1000(() =>
            {
                logger.LogInformation($"{count:@Count} {id:@Id} {amount:@A} {user:@U}");
                logger.LogWarning(new EventId(3, "evt"), $"{id:@Masked:mask:4..8}");
            });
        }

        // Batch buffers are recycled, so the only allowance is the free list growing while the
        // HTTP worker catches up; everything per log must be allocation-free.
        Assert.InRange(allocated, 0, 1024);
    }
}
