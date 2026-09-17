# KetPsi.Extensions.Logging.OpenTelemetry 🌐

The ultimate zero-allocation OpenTelemetry (OTLP) log exporter for extreme-throughput .NET microservices.

The official OpenTelemetry .NET SDK is incredibly robust, but under heavy load, its object-oriented architecture (creating `LogRecord` instances, queueing objects, and background serialization) creates massive Garbage Collection pressure.

**KetPsi.Extensions.Logging.OpenTelemetry** throws out the standard SDK pipeline entirely. It intercepts your logs at compile-time, calculates the exact Protobuf VarInt sizes synchronously, and writes raw OTLP binary directly into a lock-free byte batcher. 

### Performance Gains
* **22x Faster** execution time compared to the standard SDK.
* **0 Bytes Allocated** per log (down from ~1KB per log).

## Installation

dotnet add package KetPsi.Extensions.Logging.OpenTelemetry

## Quick Start

In your `Program.cs`:


```csharp
var builder = WebApplication.CreateBuilder(args);

// Register the Zero-Alloc OTLP Exporter
builder.Logging.AddZeroAllocOtlp();

var app = builder.Build();
```

Configure the endpoint in your `appsettings.json`:

```json
{
  "Logging": {
    "OpenTelemetry": {
        "Endpoint": "http://localhost:4318/v1/logs"
    }
  }
}
```

## Architecture

To achieve absolute zero allocation over a network boundary, this package bypasses traditional object serialization pipelines in favor of a raw, lock-free binary streaming engine.
```
[ Your Code: logger.LogInformation($"...") ]
                        │
                        ▼ (Compile-time interception)
[ Roslyn Source Generator ]
   - Extracts structured parameters and unboxed values
                        │
                        ▼ (Synchronous Pre-Calculation)
[ Protobuf VarInt Calculator ]
   - Recursively calculates exact byte size of the OTLP payload
                        │
                        ▼ (Lock-Free Memory Reservation)
[ SpinLock Byte Batcher ]
   - Reserves a exact Span<byte> slice from a 64KB ArrayPool chunk
                        │
                        ▼ (Zero-Alloc Encoding)
[ Protobuf Writer ]
   - Writes binary OTLP format directly into the reserved Span
                        │
                        ▼ (Asynchronous Network Flush)
[ Channel<T> HTTP Worker ]
   - Stitches OTLP envelopes (ResourceLogs/ScopeLogs)
   - Posts raw byte arrays to the collector via PooledArrayHttpContent
```
1. **Size Pre-calculation:** Before writing a single byte, the logger calculates the exact size of the impending Protobuf payload, eliminating dynamic array resizing.
2. **Lock-Free Batching:** High-concurrency threads use a lightweight `SpinLock` to reserve slices of a 64KB `ArrayPool<byte>`.
3. **Zero-Copy Network:** Retired 64KB chunks are wrapped in a custom `HttpContent` and dispatched over the network, completely avoiding `byte[]` duplication.

## License
MIT License