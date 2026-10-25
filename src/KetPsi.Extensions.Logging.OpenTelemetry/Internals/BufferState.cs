namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals
{
    /// <summary>
    /// One batch buffer. Standard-size buffers are recycled by <see cref="OtlpBatcher"/> (reset and reused),
    /// so neither this object nor its array is allocated per batch once the batcher is warm.
    /// </summary>
    internal sealed class BufferState(int capacity, bool pooled = false)
    {
        public byte[] Array = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);
        public int BytesWritten;
        public int PendingWriters;
        public volatile bool IsRetired;

        /// <summary>Set atomically when the buffer is handed to the export queue (guards double dispatch).</summary>
        public int Dispatched;

        /// <summary>Whether the batcher keeps this buffer for reuse (standard capacity) or releases it after use.</summary>
        public bool Pooled { get; } = pooled;

        /// <summary>
        /// InstrumentationScope envelope used for this buffer (scope.name = logger category).
        /// Set on the first Reserve that owns the buffer; subsequent writes with a different
        /// scope force a flush so each ScopeLogs group stays homogeneous.
        /// </summary>
        public byte[]? ScopeBytes;

        public void Reset()
        {
            BytesWritten = 0;
            PendingWriters = 0;
            IsRetired = false;
            Dispatched = 0;
            ScopeBytes = null;
        }

        public void Release()
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(Array);
            Array = [];
        }
    }
}
