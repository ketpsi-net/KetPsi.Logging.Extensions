# KetPsi.Extensions.Logging

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)

**KetPsi.Extensions.Logging** is a compile-time, zero-allocation structured logging engine for .NET. 

It eliminates the tension between **Developer Experience (DX)** and **bare-metal runtime performance**. By pairing **C# 12 Interceptors** and **Roslyn Source Generators** with direct UTF-8 OS-stream writing, `KetPsi` allows you to write clean interpolated strings (`$"..."`) without breaking structured telemetry and without allocating memory on the heap.

---

## The Problem

Every .NET engineer knows the rule: **never use string interpolation in `ILogger` calls.**

```csharp
// ❌ WRONG: Allocates strings, destroys structured keys, burns GC Gen0
logger.LogInformation($"Order {orderId} failed for user {userId}");

// ⚠️ TEDIOUS: Manual parameter matching, easy to introduce mismatched bug counts
logger.LogInformation("Order {OrderId} failed for user {UserId}", orderId, userId);

// ⚠️ VERBOSE: High performance, but requires fragmented partial classes/methods
[LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Order {OrderId}...")]
public static partial void LogOrderFailed(ILogger logger, string orderId, string userId);
```

`KetPsi` replaces this compromise entirely.

---

## Features

- **Ergonomic String Interpolation:** Write natural `$"{value}"` syntax without string concatenation allocations.
- **Explicit Structured Keys:** Set telemetry property names inline using the `@` prefix (`{orderId:@OrderId}`).
- **Compile-Time Interception:** Hijacks log invocations using C# 12 Interceptors—no reflection or runtime parsing overhead.
- **True Zero-Allocation Core:** Value types pass as custom unboxed structs down to UTF-8 byte buffers.
- **Inline Masking & Transformations:** Mask sensitive data (`:mask:4..8`) directly in the template with zero heap allocations.
- **Inline Zero-Alloc JSON:** Serialize objects (`:json`) directly into native byte writers.
- **Full Microsoft Ecosystem Alignment:** Respects `appsettings.json` hot-reloading, log levels, namespace filters, and DI options pattern via `[ProviderAlias("Console")]`.

---

## Benchmark Comparison

Benchmark executed using **BenchmarkDotNet v0.14.0** on .NET 8 / 9 (x64):

| Method                                        | Mean       | Ratio | Gen0   | Allocated | Alloc Ratio |
|:--------------------------------------------- |-----------:|------:|-------:|----------:|------------:|
| Microsoft Standard LogInformation             |   518.4 ns |  1.11 | 0.0286 |     376 B |        1.07 |
| Microsoft LoggerMessage SourceGen             |   472.0 ns |  1.01 | 0.0267 |     352 B |        1.00 |
| **KetPsi Interpolated LogInformation**        | **254.1 ns** | **0.54** |      **-** |      **0 B** |    **0.00** |
|                                               |            |       |        |           |             |
| Microsoft Complex Log                         |   596.1 ns |  1.28 | 0.0381 |     504 B |        1.43 |
| Microsoft Complex LoggerMessage               |   644.5 ns |  1.38 | 0.0362 |     480 B |        1.36 |
| **KetPsi Complex Interpolated**               | **279.6 ns** | **0.60** |      **-** |      **0 B** |    **0.00** |
|                                               |            |       |        |           |             |
| Microsoft Complex With JSON                   | 1,036.3 ns |  2.22 | 0.0553 |     736 B |        2.09 |
| Microsoft Complex LoggerMessage + JSON        |   940.6 ns |  2.01 | 0.0534 |     712 B |        2.02 |
| **KetPsi Complex Interpolated + JSON**        | **532.9 ns** | **1.14** |      **-** |     **15 B\***|    **0.04** |
|                                               |            |       |        |           |             |
| Microsoft Scoped Logging                      |   628.0 ns |  1.34 | 0.0372 |     496 B |        1.41 |
| **KetPsi Scoped Logging**                     | **340.8 ns** | **0.73** | 0.0052 |     74 B**|    **0.21** |

> *\* Note: 15 B represents the benchmark test object instantiation; framework allocation is 0 B.*  
> *\*\* Note: 74 B is the unavoidable .NET CLR `AsyncLocal` context-flow tax.*

---

## Installation & Setup

### 1. Enable Interceptors in your `.csproj`
C# 12 Interceptors require explicit compiler opt-in:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Extensions.Logging</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

### 2. Install the Package
```bash
dotnet add package KetPsi.Extensions.Logging
```

### 3. Register the Provider
In your `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register the Zero-Alloc Console Logger
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();
```

---

## Syntax Guide

### 1. Standard Structured Log
Prefix property names with `@` inside interpolation expressions:

```csharp
logger.LogInformation($"Processed order {orderId:@OrderId} for customer {customerId:@CustomerId} with amount {amount:@Amount:N0}");
```
*Emits structured properties `OrderId` and `CustomerId` without parameter matching bugs.*

---

### 2. Sensitive Data Masking (`:mask:start..end`)
Mask credit cards, tokens, or personal identifiers without creating temporary substring allocations:

```csharp
// Masks characters between index 4 and index 8
logger.LogInformation($"Payment processed on card {cardNumber:@Card:mask:4..8}");
// Output: Payment processed on card 4532****1092
```

---

### 3. Zero-Alloc Inline JSON (`:json`)
Serializes complex records/DTOs directly into UTF-8 stream buffers without intermediate string allocations:

```csharp
var payload = new TransactionPayload(Amount: 150.50m, Currency: "USD");

logger.LogInformation($"Incoming checkout {payload:@Payload:json}");
```

---

### 4. Zero-Alloc Scopes
```csharp
using (logger.StartScope($"Tenant: {tenantId:@TenantId}"))
{
    logger.LogInformation("Processing tenant jobs...");
}
```

---

## Configuration (`appsettings.json`)

`KetPsi` uses the `[ProviderAlias("Console")]` metadata, making it a drop-in replacement for standard configuration sections:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    },
    "Console": {
      "FormatterName": "simple",
      "LogToStandardErrorThreshold": "Error",
      "FormatterOptions": {
        "SingleLine": false,
        "IncludeCategory": true,
        "IncludeEventId": false,
        "IncludeScopes": true,
        "TimestampFormat": "yyyy-MM-dd HH:mm:ss.fff "
      }
    }
  }
}
```

Configuration reloads are monitored live via `IOptionsMonitor`—log level adjustments applied to `appsettings.json` take effect immediately without restarting the host.

---

## Architecture

```text
[ Your Code: logger.LogInformation($"...") ]
                        │
                        ▼ (Compile-time interception)
[ Roslyn Source Generator / Interceptor ]
   - Extracts keys (@OrderId)
   - Generates specialized unboxed struct
                        │
                        ▼ (Zero-Boxing Pass via Generic Delegate Cache)
[ ZeroAllocationConsoleLogger ]
   - Validates log levels via Microsoft filter engine
   - Formats to ArrayBufferWriter<byte> in UTF-8
                        │
                        ▼ (ThreadStatic Buffer / Raw Byte Stream)
[ ConsoleLogProcessor ]
   - Direct flush to stdout / stderr stream handles
```

1. **Roslyn Interceptor:** Hijacks the call site at compile time, bypassing `params object[]` boxing and parameter allocation.
2. **Static Generic Delegate Cache:** Invokes `IDeferredUtf8Formatter.FormatTo` without casting struct instances to interface types (which causes heap boxing).
3. **Direct Byte Pipeline:** Streams raw UTF-8 spans directly to the OS console handles via a background channel consumer.

---

## Roadmap

- [x] C# 12 Interceptors and Roslyn Generator core pipeline.
- [x] Custom zero-allocation `ConsoleLoggerProvider` drop-in replacement.
- [x] In-template `:mask` and `:json` formatters.
- [x] Comprehensive options configuration & live hot-reload support.
- [ ] Standalone Console Application hosting support (non-Generic Host).
- [ ] **OpenTelemetry Exporter Integration:** Custom zero-allocation exporter bridging directly to OTLP streams without boxing attributes.

---

## License

This project is licensed under the [MIT License](LICENSE).