---
name: ketpsi-logging
description: How to add, configure and write log calls with the KetPsi zero-allocation logging packages (KetPsi.Extensions.Logging.Abstractions, .Console, .OpenTelemetry). Use when a .NET project references any KetPsi.Extensions.Logging package, when the user asks for zero-allocation / interceptor-based structured logging, or when writing `logger.LogX($"...")`, `logger.StartScope($"...")`, `AddZeroAllocConsole` or `AddZeroAllocOtlp` code.
---

# Using KetPsi zero-allocation logging

KetPsi turns ordinary interpolated-string log calls into zero-allocation structured logs. At compile time, a Roslyn source generator uses C# 12 interceptors to rewrite every `logger.LogInformation($"...")` call site. The generated code hands the logger provider a strongly typed struct, so there is no `params object[]`, no boxing and no string building.

| Package | Purpose |
|---|---|
| `KetPsi.Extensions.Logging.Abstractions` | Interpolated-string handlers, `LogX` / `StartScope` overloads, and the source generator (shipped as an analyzer). Pulled in transitively by the two packages below. |
| `KetPsi.Extensions.Logging.Console` | Zero-alloc console provider. Drop-in replacement for the MEL console logger that reads the same `Logging:Console` config. |
| `KetPsi.Extensions.Logging.OpenTelemetry` | Zero-alloc OTLP/HTTP protobuf log exporter. Does not use the OpenTelemetry SDK. |

Target frameworks: net8.0, net9.0, net10.0.

## 1. Project setup (required)

```bash
dotnet add package KetPsi.Extensions.Logging.Console
dotnet add package KetPsi.Extensions.Logging.OpenTelemetry   # optional
```

Every project that **contains log calls** must opt in to the interceptor namespace:

```xml
<PropertyGroup>
  <LangVersion>latest</LangVersion> <!-- C# 12+ -->
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Extensions.Logging</InterceptorsPreviewNamespaces>
  <!-- Optional: inspect generated interceptors under obj/.../generated -->
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

> ⚠️ Without this property the compiler rejects the generated interceptors. If the generator itself fails to load, the compiler shows a `CS8032` warning, and KetPsi's `LogX($"...")` overloads then only release their buffers and **log nothing**. That usually means the SDK is too old for the analyzer's Roslyn version, so use the .NET 10 SDK.

## 2. Register providers

```csharp
using KetPsi.Extensions.Logging.Console;
using KetPsi.Extensions.Logging.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Logging
    .ClearProviders()
    .AddZeroAllocConsole()
    .AddZeroAllocOtlp(opt => opt
        .UseEndpoint("http://localhost:4318/v1/logs")
        .ConfigureResource(new Dictionary<string, string>
        {
            ["service.name"] = "orders-api",
            ["deployment.environment"] = "Production",
        }));
```

Level filtering uses the standard `Logging:LogLevel` rules. The providers themselves report every level except `None` as enabled.

### Console configuration (`appsettings.json`)

The console provider binds `Logging:Console`, the same section as the built-in MEL console logger:

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" },
    "Console": {
      "FormatterName": "simple",              // simple | json | systemd
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

### OpenTelemetry configuration (`appsettings.json`)

The OTLP exporter binds `Logging:OpenTelemetry`:

```json
{
  "Logging": {
    "OpenTelemetry": {
      "Endpoint": "http://localhost:4318/v1/logs",
      "QueueCapacity": 1024,
      "Resources": { "service.name": "orders-api" }
    }
  }
}
```

- The default endpoint is `http://localhost:4318/v1/logs`. OTLP/**HTTP** protobuf is the only transport (no gRPC).
- `UseEndpoint(...)` in code takes precedence over the `Endpoint` setting.
- Resources from code and from config are merged. If both set the same key, the value from code wins.
- Empty resource keys are dropped.
- Default resources `telemetry.sdk.*` and `data_stream.type=logs` are added automatically.
- Batches are flushed every 2 s, or sooner when a 64 KB buffer fills. Batch buffers are recycled, so the logging thread allocates nothing.
- `IncludeFormattedMessage(false)` (or `"IncludeFormattedMessage": false` in config) sends the message template as the body instead of the rendered message. This is the OpenTelemetry SDK's default and saves about a third of the per-log CPU. Values are still sent as attributes.
- When the collector can't keep up and the export queue is full, records are dropped *before* encoding (cheap), matching the SDK's batch processor.

## 3. Writing log calls

Pass an **interpolated string** (or a `+` chain of interpolated strings) directly to `LogTrace`, `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical` or `StartScope`. Every level supports these overloads: `(message)`, `(exception, message)`, `(eventId, message)` and `(eventId, exception, message)`.

```csharp
logger.LogInformation($"Processed order {orderId:@OrderId} for {customerId:@CustomerId}");
logger.LogError(ex, $"Payment failed for order {orderId:@OrderId}");
logger.LogWarning(new EventId(1001, "PaymentRetry"), $"Retrying {orderId:@OrderId}");
logger.LogInformation($"Long message part one {a:@A} " +
                      $"part two {b:@B}");   // concatenation is intercepted too
```

Literal text can contain anything C# allows (quotes, backslashes, newlines, `{{escaped braces}}`).

### Format specifiers

Format specifiers go after the `:` in each hole:

| Syntax | Effect |
|---|---|
| `{x:@Name}` | Structured property `Name`. Prefer explicit names. |
| `{x:@Name:N2}` | Property name plus a standard .NET format string (`N2`, `yyyy-MM-dd`, `HH:mm:ss`, …). |
| `{x}` / `{x:N2}` | No `@Name`: the name comes from the expression (`orderId` → `orderId`, `order.Total` → `Total`). Other expressions, such as method calls, become `arg0`, `arg1`, …. |
| `{x,10:@Name}` / `{x,-10:@Name}` | Pads the rendered value to 10 characters (right- or left-aligned). |
| `{x:@Name:json}` | Serializes `x` to JSON inline. Exported as a single JSON string attribute. |
| `{x:@Name:json:indexed}` | JSON exported to OTLP as a nested map (kvlist), so backends can index the fields. The console prints it as plain JSON. |
| `{x:@Name:mask}` / `{x:@Name:mask:4}` | Masks every character except the last 4 (or the last N). |
| `{x:@Name:mask:4..8}` | Masks characters `[4, 8)`. Range syntax follows C# `Index`/`Range`: `..4` masks the first 4, `4..` masks from index 4 to the end, `^4..` masks the last 4. |
| `{x:@Name:D6:mask:..2}` | Applies the standard format first, then masks the result. |

An invalid mask range produces warning `KPL002` and falls back to `mask` (all but the last 4).

Message templates are rebuilt with `{Name}` placeholders. They are exported as `{OriginalFormat}`, which matches the standard .NET convention.

### Scopes

```csharp
using (logger.StartScope($"Tenant {tenantId:@TenantId}"))
{
    logger.LogInformation($"Processing job {jobId:@JobId}");
}

// Middleware: await inside the using block so the scope stays active for the whole request
using (logger.StartScope($"{user:@User:json:indexed}"))
{
    await next(context);
}
```

Scope properties are attached to every log written inside the scope. The OTLP exporter writes them as log attributes, and the console prints them when `IncludeScopes` is on. `StartScope` returns `IDisposable?`.

### JSON without reflection (trimming/AOT)

Register a source-generated `JsonSerializerContext` once, anywhere in the assembly that contains the log calls. Either form works:

```csharp
[JsonSerializable(typeof(PaymentPayload))]
internal partial class AppJsonContext : JsonSerializerContext;

// Option A: compile-time marker
logger.UseSerializationContext<AppJsonContext>();

// Option B: console config
builder.Logging.AddZeroAllocConsole(c => c.UseSerializationContext(AppJsonContext.Default));
```

The generator wires the context into every `:json` call site and also sets it as the runtime default. Without a context, `:json` falls back to reflection-based `JsonSerializer.Serialize`. That fallback fails under NativeAOT and is the only source of KetPsi's own trim/AOT warnings. Every type you log with `:json` must be registered on the context.

### NativeAOT and trimming

The packages are NativeAOT-safe. Generated code registers each log-state type and each logged value type in a module initializer, so the providers never use `MakeGenericMethod` or other runtime reflection. Builds may still show `IL2026`/`IL3050` warnings for:

- the `:json` reflection fallback (fixed by registering a serializer context);
- `AddZeroAllocConsole` / `AddZeroAllocOtlp` binding options from configuration. To clear these, enable the configuration binding source generator in the app: `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`.

## 4. OTLP record shape

| Field | Value |
|---|---|
| `scope.name` / `attributes.logger` | Logger category |
| `body` | Rendered message |
| `attributes.{OriginalFormat}` | Message template |
| `attributes.<Name>` | Each `@Name` property, with its native type kept |
| `attributes.event.id` / `event.name` | When an `EventId` is present |
| `attributes.exception.type` / `exception.message` | When an exception is passed |
| `trace_id` / `span_id` | From `Activity.Current` |

## 5. Verifying it works

- Set `EmitCompilerGeneratedFiles=true` and look for `obj/<cfg>/<tfm>/generated/KetPsi.Extensions.Logging.Generator/.../<Namespace_Class>_LogInterceptors.g.cs`. Each intercepted call shows up there as an `[InterceptsLocation]` method plus a matching `LogState_N` struct.
- A call that binds to a KetPsi overload but can't be intercepted is a **build error** (`KPL001`/`KPL003`), not a silent no-op.

## 6. Diagnostics and remaining limits

| Id | Severity | Meaning | Fix |
|---|---|---|---|
| `KPL001` | Error | A logged **value type** is not accessible to generated code: a `private`/`protected` nested struct, a `file`-local struct, or a generic type parameter that isn't constrained to `class`. | Make the struct `internal` or `public`, or log one of its members. |
| `KPL002` | Warning | Invalid `mask` range. | Use `mask`, `mask:N`, or `mask:start..end` with integer or `^integer` bounds. |
| `KPL003` | Error | A KetPsi overload was called with something other than an interpolated string, such as a pre-built string variable. | Pass the interpolated string directly. |

- Inaccessible **reference** types (private nested classes, anonymous types) are fine. They are logged as `object`: formatted through `ToString()`, and `:json` serializes their runtime type.
- Plain MEL calls (non-interpolated templates, `LoggerMessage`, framework logs) still work. They go through each provider's fallback path, which is correct but allocates.
- Structs that contain references (for example `KeyValuePair<string, int>` or a record struct with a `string`) are boxed by the interpolated-string handler. Log their members instead to stay at zero allocations.
- `:json:indexed` allocates while expanding JSON into OTLP attributes. `:json` (a single string attribute) does not.
- `AddZeroAllocConsole` reads `IConfiguration` from DI, so register it when you don't use a generic host.
