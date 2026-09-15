using System.Net.Http.Headers;

namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals;

internal sealed class PooledArrayHttpContent : HttpContent
{
    private readonly byte[] _buffer;
    private readonly int _length;

    public PooledArrayHttpContent(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
        Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        await stream.WriteAsync(_buffer.AsMemory(0, _length)).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}