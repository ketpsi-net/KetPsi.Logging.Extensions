namespace KetPsi.Extensions.Logging.OpenTelemetry.Internals
{
    internal sealed class KetPsiOtlpOptions
    {
        /// <summary>
        /// The destination endpoint for the OpenTelemetry Collector.
        /// Defaults to the standard local gRPC/HTTP port.
        /// </summary>
        public string Endpoint { get; set; } = "http://localhost:4318/v1/logs";

        /// <summary>
        /// The maximum number of 64KB buffers that can be queued before the lock-free channel blocks.
        /// </summary>
        public int QueueCapacity { get; set; } = 1024;

        public Dictionary<string, string> Resources { get; set; } = [];
    }
}
