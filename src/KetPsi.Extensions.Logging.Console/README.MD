# KetPsi.Extensions.Logging.Console ⚡

A hyper-optimized, zero-allocation console logger for .NET, built on top of `KetPsi.Extensions.Logging.Abstractions`.

When running in containerized environments (Docker/Kubernetes), stdout logging is standard practice. However, standard console loggers allocate strings, arrays, and background objects for every single log message. This package intercepts your log messages and writes them directly to the standard output buffer using UTF-8 `Span<byte>` and stack-allocated memory, resulting in a **0-byte allocation footprint per log**.

## Installation

dotnet add package KetPsi.Extensions.Logging.Console

## Quick Start

In your `Program.cs`:

var builder = WebApplication.CreateBuilder(args);

// Clear default loggers and register the Zero-Alloc Console Logger
builder.Logging.ClearProviders();
builder.Logging.AddZeroAllocConsole();

var app = builder.Build();

## Configuration (`appsettings.json`)

`KetPsi.Extensions.Logging.Console` uses the `[ProviderAlias("Console")]` metadata, making it a drop-in replacement for standard configuration sections:

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

## Architecture

```
[ Your Code: logger.LogInformation($"...") ]
                        │
                        ▼ (Compile-time interception via Abstractions)
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
   - Direct flush to stdout / stderr stream handles via background channel

```
1. **Roslyn Interceptor:** Hijacks the call site at compile time, bypassing `params object[]` boxing and parameter allocation.
2. **Static Generic Delegate Cache:** Invokes `IDeferredUtf8Formatter.FormatTo` without casting struct instances to interface types (which causes heap boxing).
3. **Direct Byte Pipeline:** Streams raw UTF-8 spans directly to the OS console handles via a background channel consumer.

## License
MIT License