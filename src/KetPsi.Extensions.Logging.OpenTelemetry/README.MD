# KetPsi.Extensions.Logging.OpenTelemetry 🌐

The ultimate zero-allocation OpenTelemetry (OTLP) log exporter for extreme-throughput .NET microservices.

The official OpenTelemetry .NET SDK is robust, but under heavy load its object-oriented pipeline (creating `LogRecord` instances, queueing objects, background serialization) creates significant GC pressure.

**KetPsi.Extensions.Logging.OpenTelemetry** bypasses that pipeline: compile-time interception, exact Protobuf size calculation, and raw OTLP binary written into a lock-free byte batcher.

### Performance Gains
* **22x Faster** execution time compared to the standard SDK.
* **0 Bytes Allocated** per log on the hot path (down from ~1KB per log).

## Installation

```bash
dotnet add package KetPsi.Extensions.Logging.OpenTelemetry
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddZeroAllocOtlp(opt => opt
    .UseEndpoint("http://localhost:4318/v1/logs")
    .ConfigureResource(new Dictionary<string, string>
    {
        ["service.name"] = "api.gateway",
        ["deployment.environment"] = "Production"
    }));

var app = builder.Build();
```

Or configure the endpoint in `appsettings.json`:

```json
{
  "Logging": {
    "OpenTelemetry": {
      "Endpoint": "http://localhost:4318/v1/logs",
      "Resources": {
        "service.name": "api.gateway"
      }
    }
  }
}
```

## OTLP payload shape (aligned with the standard .NET exporter)

| Field | Source |
|-------|--------|
| `scope.name` | Logger category name (e.g. `Microsoft.AspNetCore.Routing.EndpointMiddleware`) |
| `body` | **Rendered** message text |
| `attributes.{OriginalFormat}` | Message template when it differs from the body |
| `attributes.logger` | Logger category (also in `scope.name`) |
| `attributes.event.id` / `event.name` | `EventId` when present |
| `event_name` | `EventId.Name` on the LogRecord (OTLP field 12) |
| `attributes.exception.*` | Exception type / message when an exception is logged |
| Active `StartScope` / `BeginScope` | Exported as log attributes |

### Structured formats

```csharp
// Single JSON string attribute
logger.LogInformation($"User {user:@user:json}");

// Nested OTLP map (kvlist) under "user" — backends may flatten for search
logger.LogInformation($"User {user:@user:json:indexed}");

// Scopes (await next so the scope stays active for the request)
using (logger.StartScope($"{user:@user:json:indexed}"))
{
    await next(context);
}
```

Empty resource attribute keys are rejected (Elasticsearch-safe).

## Architecture

```
[ Your Code: logger.LogInformation($"...") ]
                        │
                        ▼ (Compile-time interception)
[ Roslyn Source Generator ]
                        │
                        ▼
[ Protobuf size pre-calculation + Span write ]
                        │
                        ▼
[ Lock-free batcher (per scope.name / category) ]
                        │
                        ▼
[ HTTP worker → OTLP/HTTP protobuf ]
```

## License
MIT License
