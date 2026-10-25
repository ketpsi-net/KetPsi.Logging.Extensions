namespace KetPsi.Extensions.Logging.OpenTelemetry
{
    public interface IZeroAllocOtlpOptionsBuilder
    {
        IZeroAllocOtlpOptionsBuilder UseEndpoint(string endpoint);

        IZeroAllocOtlpOptionsBuilder SetBatchSizeLimit(int batchSizeLimit);

        IZeroAllocOtlpOptionsBuilder ConfigureResource(Dictionary<string, string> resources);

        /// <summary>
        /// When <see langword="true"/> (default) the log body is the rendered message. When <see langword="false"/>
        /// the body is the message template, as the OpenTelemetry .NET SDK sends by default, which skips rendering
        /// the message on the calling thread (attributes still carry every value).
        /// </summary>
        IZeroAllocOtlpOptionsBuilder IncludeFormattedMessage(bool include = true);
    }
    internal class ZeroAllocOtlpOptionsBuilder : IZeroAllocOtlpOptionsBuilder
    {
        private ZeroAllocOtlpOptions _options = new();
        private Dictionary<string, string> _resources = new();

        private void AddResource(string key, string? value)
        {
            // Empty keys become resource.attributes[""] which Elasticsearch rejects.
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (_resources.ContainsKey(key))
                return;

            _resources.Add(key, value ?? string.Empty);
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
            if (resources is null)
                return this;

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

        public IZeroAllocOtlpOptionsBuilder IncludeFormattedMessage(bool include = true)
        {
            _options.IncludeFormattedMessage = include;
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
            var otlpResourceBuilder = new OtlpResourceBuilder(Math.Max(4096, _resources.Count * 64));
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

        /// <summary>Null when not set in code (the appsettings value, or the default, applies).</summary>
        public bool? IncludeFormattedMessage { get; set; }

        public byte[] Resources { get; set; } = [];
    }
}
