namespace KetPsi.Extensions.Logging.Abstractions;

public interface IDeferredUtf8Formatter
{
    void FormatTo(System.Buffers.IBufferWriter<byte> writer);
}
