namespace KetPsi.Extensions.Logging.OpenTelemetry
{
    public interface IZeroAllocOtlpOptionsBuilder
    {
        IZeroAllocOtlpOptionsBuilder UseEndpoint(string endpoint);

        IZeroAllocOtlpOptionsBuilder SetBatchSizeLimit(int batchSizeLimit);

        IZeroAllocOtlpOptionsBuilder ConfigureResource(Dictionary<string, string> resources);
    }
    internal class ZeroAllocOtlpOptionsBuilder : IZeroAllocOtlpOptionsBuilder
    {
        private ZeroAllocOtlpOptions _options = new();
        private Dictionary<string, string> _resources = new();
        private void AddResource(string key, string value)
        {
            if (_resources.ContainsKey(key))
                return;
            _resources.Add(key, value);
        }
        private void AddDefaultResources()
        {
            AddResource("telemetry.sdk.name", "opentelemetry");
            AddResource("telemetry.sdk.language", "dotnet");
            AddResource("telemetry.sdk.version", "1.0.0");
            AddResource("data_stream.type", "logs");
        }

        public IZeroAllocOtlpOptionsBuilder ConfigureResource(Dictionary<string, string> resources)
        {
            foreach (var kv in resources)
            {
                AddResource(kv.Key, kv.Value);
            }
            return this;
        }

        public IZeroAllocOtlpOptionsBuilder SetBatchSizeLimit(int batchSizeLimit)
        {
            _options.BatchSizeLimit = batchSizeLimit;
            return this;
        }

        public IZeroAllocOtlpOptionsBuilder UseEndpoint(string endpoint)
        {
            _options.Endpoint = endpoint;
            return this;
        }

        internal ZeroAllocOtlpOptions Build()
        {
            AddDefaultResources();
            var otlpResourceBuilder = new OtlpResourceBuilder(_resources.Count);
            foreach (var item in _resources)
            {
                otlpResourceBuilder.AddAttribute(item.Key, item.Value);
            }
            otlpResourceBuilder.Build();

            _options.Resources = otlpResourceBuilder.ResourceBytes;
            return _options;
        }
    }
    internal class ZeroAllocOtlpOptions()
    {
        public string? Endpoint { get; set; }
        public int BatchSizeLimit { get; set; }

        public byte[] Resources { get; set; } = [];
    }


}
