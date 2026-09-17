# KetPsi.Extensions.Logging 🚀

[![NuGet](https://img.shields.io/nuget/v/KetPsi.Extensions.Logging.Abstractions.svg)](https://www.nuget.org/packages/KetPsi.Extensions.Logging.Abstractions/)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

**KetPsi.Extensions.Logging** is a compile-time, zero-allocation structured logging engine for .NET. 

It eliminates the tension between **Developer Experience (DX)** and **bare-metal runtime performance**. By pairing **C# 12 Interceptors** and **Roslyn Source Generators** with direct UTF-8 OS-stream writing, `KetPsi` allows you to write clean interpolated strings (`$"..."`) without breaking structured telemetry and without allocating memory on the heap.

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

## Key Features

- **Ergonomic String Interpolation:** Write natural `$"{value}"` syntax without string concatenation allocations.
- **Explicit Structured Keys:** Set telemetry property names inline using the `@` prefix (`{orderId:@OrderId}`).
- **Zero Boxing:** Native types (`int`, `Guid`, `DateTime`, etc.) remain on the stack.
- **Inline Masking & Transformations:** Mask sensitive data (`:mask:4..8`) directly in the template with zero heap allocations.
- **Inline Zero-Alloc JSON:** Serialize objects (`:json`) directly into native byte writers.

## Benchmark Comparison

Benchmark executed using **BenchmarkDotNet v0.14.0** on .NET 8 / 9 (x64) on Conolse logger:

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

And for the OpenTelemetry logger:

| Method                                 | Mean          | Error       | StdDev     | Ratio  | RatioSD  | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------------------------|--------------:|------------:|-----------:|-------:|---------:|-------:|-------:|----------:|------------:|
| Microsoft Standard LogInformation      | 860.70 ns     | 13.031 ns   | 11.552 ns  |  1.00  |    0.02  | 0.0782 | 0.0381 |    1024 B |        1.00 |
| **KetPsi Interpolated LogInformation** |  **39.10 ns** |**0.419 ns** |**0.350 ns**|**0.05**| **0.00** |**-**   | **-**  | **-**     |**0.00**     |

---

## Usage & Setup

This package is typically consumed implicitly by the specific KetPsi logger implementations (Console, OpenTelemetry). However, you must enable C# 12 Interceptors in your `.csproj` to allow the Roslyn generator to hijack the log calls:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <!-- REQUIRED: Opt-in to C# 12 Interceptors -->
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Extensions.Logging</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

Once configured, standard string interpolation is transformed into a zero-allocation operation:

```csharp
logger.LogInformation($"Processed order {orderId:@OrderId} for customer {customerId:@CustomerId}");
```

For configuring console logger read: [(ConsolseLogger configuration)](https://github.com/ketpsi-net/KetPsi.Logging.Extensions/tree/main/src/KetPsi.Extensions.Logging.Console)

For configurting OpenTelemtery logger read: [(OpenTelemetryLogger configuration)](https://github.com/ketpsi-net/KetPsi.Logging.Extensions/tree/main/src/KetPsi.Extensions.Logging.OpenTelemetry)

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

## License
MIT License