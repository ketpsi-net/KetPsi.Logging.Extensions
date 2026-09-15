namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals
{
    internal sealed class BufferState(int capacity)
    {
        public readonly byte[] Array = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);
        public int BytesWritten;
        public int PendingWriters;
        public volatile bool IsRetired;
    }
}
