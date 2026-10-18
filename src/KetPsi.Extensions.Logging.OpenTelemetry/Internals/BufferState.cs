namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals
{
    internal sealed class BufferState(int capacity)
    {
        public readonly byte[] Array = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);
        public int BytesWritten;
        public int PendingWriters;
        public volatile bool IsRetired;

        /// <summary>
        /// InstrumentationScope envelope used for this buffer (scope.name = logger category).
        /// Set on the first Reserve that owns the buffer; subsequent writes with a different
        /// scope force a flush so each ScopeLogs group stays homogeneous.
        /// </summary>
        public byte[]? ScopeBytes;
    }
}
