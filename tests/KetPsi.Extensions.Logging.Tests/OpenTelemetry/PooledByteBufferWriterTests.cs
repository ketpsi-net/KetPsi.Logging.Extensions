using System;
using System.Buffers;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

namespace OpenTelemetry;

public class PooledByteBufferWriterTests
{
    [Fact]
    public void GetSpan_Zero_WhenFull_ReturnsNonEmptySpan()
    {
        using var writer = new PooledByteBufferWriter(16);
        writer.Advance(writer.GetSpan(16).Length); // fill the rented buffer completely

        Assert.False(writer.GetSpan(0).IsEmpty);
    }

    [Fact]
    public void Write_ExtensionMethod_GrowsAcrossFullBuffer()
    {
        using var writer = new PooledByteBufferWriter(16);
        writer.Advance(writer.GetSpan(16).Length);

        var payload = new byte[100];
        payload.AsSpan().Fill(7);
        writer.Write(payload);

        Assert.Equal(payload, writer.WrittenSpan[^100..].ToArray());
    }
}
