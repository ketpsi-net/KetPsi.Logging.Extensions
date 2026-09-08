using System;
using System.IO;
using System.Text.Json.Serialization;

using BenchmarkDotNet.Attributes;

using KetPsi.Extensions.Logging.Abstractions;
using KetPsi.Extensions.Logging.Console.Internals.Formatters;
using KetPsi.Extensions.Logging.Console.Internals.ZeroAllocLogger;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
public class PaymentPayload
{
    public decimal Amount { get; set; } = 10000;
    public string Currency { get; set; } = "";
}

[JsonSerializable(typeof(PaymentPayload))]
public partial class BenchmarkJsonContext : JsonSerializerContext { }
// Generates the allocation memory footprint
[MemoryDiagnoser]
//[ShortRunJob]
public partial class LoggerBenchmarks
{
    private ILogger<LoggerBenchmarks> _msLogger = null!;
    private ILogger<LoggerBenchmarks> _zeroLogger = null!;

    private readonly PaymentPayload _payload = new() { Amount = 150.75m, Currency = "USD" };
    private readonly string _cardNumber = "4111222233334444";
    private readonly int _paymentId = 98765;

    [GlobalSetup]
    public void Setup()
    {
        // Redirect standard Console to Null so MS Logger doesn't flood the terminal
        Console.SetOut(TextWriter.Null);

        var msServices = new ServiceCollection();
        msServices.AddLogging(builder =>
        {
            builder.AddConsole();
        });
        _msLogger = msServices.BuildServiceProvider().GetRequiredService<ILogger<LoggerBenchmarks>>();

        var zeroServices = new ServiceCollection();
        zeroServices.AddLogging(builder =>
        {
            builder.ClearProviders();
            // Inject Stream.Null directly to bypass OS stdout
            builder.Services.AddSingleton<ILoggerProvider>(srv =>
                new ZeroAllocationConsoleLoggerProvider(new SimpleConsoleFormatter(srv.GetService<IOptionsMonitor<ZeroAllocConsoleFormatterOptions>>()), Stream.Null, Stream.Null, srv.GetService<IOptionsMonitor<ConsoleLoggerOptions>>()));
        });
        _zeroLogger = zeroServices.BuildServiceProvider().GetRequiredService<ILogger<LoggerBenchmarks>>();
        JsonLogValueSerializationContext.Context = BenchmarkJsonContext.Default;

    }

    // -------------------------------------------------------------------
    // BENCHMARK 1: Traditional (Heaviest Allocation)
    // -------------------------------------------------------------------

    [Benchmark]
    public void Standard_LogInformation()
    {
        int arg1 = 1234;
        string arg2 = "PaymentService";

        // Allocates an object[] array.
        // Boxes the integer '1234' into an object.
        // Parses the template string at runtime.
        _msLogger.LogInformation("Start the process with id {Id} for {Service}", arg1, arg2);
    }

    // -------------------------------------------------------------------
    // BENCHMARK 2: Microsoft Source Generator (Current Baseline)
    // -------------------------------------------------------------------

    [Benchmark(Baseline = true)]
    public void LoggerMessage_LogInformation()
    {
        // Avoids boxing and object[] allocation, but may still allocate 
        // the final rendered string internally before writing.
        LogMicrosoft(_msLogger, 1234, "PaymentService");
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Start the process with id {Id} for {Service}")]
    private static partial void LogMicrosoft(ILogger logger, int id, string service);

    // -------------------------------------------------------------------
    // BENCHMARK 3: Zero-Alloc Interceptor (Fastest)
    // -------------------------------------------------------------------

    [Benchmark()]
    public void Interpolated_LogInformation()
    {
        int arg1 = 1234;
        string arg2 = "PaymentService";

        // Intercepted at compile time. 
        // Zero boxing, zero string formatting, direct memory writing.
        _zeroLogger.LogInformation($"Start the process with id {arg1} for {arg2}");
    }

    [Benchmark]
    public void Standard_Complex_LogInformation()
    {
        // 1. Manually allocate a masked string
        string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
        {
            card.AsSpan().CopyTo(span);
            span[..^4].Fill('*'); // Mask everything but last 4
        });

        // 2. Allocate object[], box the integer, parse template
        _msLogger.LogInformation("Processing payment {Id} for card {Card} with payload {Payload}",
            _paymentId, maskedCard, string.Empty);
    }

    // -------------------------------------------------------------------
    // BENCHMARK 5: Microsoft Source Generator Complex
    // -------------------------------------------------------------------

    [Benchmark]
    public void LoggerMessage_Complex_LogInformation()
    {
        // We STILL have to allocate the strings manually before passing them in
        string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
        {
            card.AsSpan().CopyTo(span);
            span[..^4].Fill('*');
        });

        LogComplexMicrosoft(_msLogger, _paymentId, maskedCard, string.Empty);
    }


    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Processing payment {Id} for card {Card} with payload {Payload}")]
    private static partial void LogComplexMicrosoft(ILogger logger, int id, string card, string payload);

    // -------------------------------------------------------------------
    // BENCHMARK 6: Zero-Alloc Source Generator
    // -------------------------------------------------------------------

    [Benchmark]
    public void Interpolated_Complex_LogInformation()
    {
        // Zero manual allocations. 
        // The SG parses the :mask and :json, creating the structs that write 
        // directly to the UTF-8 ArrayBufferWriter.
        _zeroLogger.LogInformation($"Processing payment {_paymentId} for card {_cardNumber:mask:..4}");
    }



    [Benchmark]
    public void Standard_Complex_WithJson_LogInformation()
    {
        // 1. Manually allocate a masked string
        string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
        {
            card.AsSpan().CopyTo(span);
            span[..^4].Fill('*'); // Mask everything but last 4
        });

        // 2. Manually allocate a UTF-16 JSON string
        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(
            _payload,
            BenchmarkJsonContext.Default.PaymentPayload);

        // 3. Allocate object[], box the integer, parse template
        _msLogger.LogInformation("Processing payment {Id} for card {Card} with payload {Payload}",
            _paymentId, maskedCard, jsonPayload);
    }
    [Benchmark]
    public void LoggerMessage_Complex_WithJson_LogInformation()
    {
        // We STILL have to allocate the strings manually before passing them in
        string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
        {
            card.AsSpan().CopyTo(span);
            span[..^4].Fill('*');
        });

        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(
            _payload,
            BenchmarkJsonContext.Default.PaymentPayload);

        LogComplexMicrosoft(_msLogger, _paymentId, maskedCard, jsonPayload);
    }
    [Benchmark]
    public void Interpolated_Complex_WithJson_LogInformation()
    {
        // Zero manual allocations. 
        // The SG parses the :mask and :json, creating the structs that write 
        // directly to the UTF-8 ArrayBufferWriter.
        _zeroLogger.LogInformation($"Processing payment {_paymentId} for card {_cardNumber:mask:..4} with payload:{_payload:@Payload:json}");
    }


    [Benchmark]
    public void Standard_ScopedLogging()
    {
        using (_msLogger.BeginScope("Transaction {TxId}", 1001))
        {
            _msLogger.LogInformation("Processing payment , {Id}", 1000);
        }
    }

    [Benchmark]
    public void Interpolated_ScopedLogging()
    {
        using (_zeroLogger.StartScope($"Transaction {1001}"))
        {
            _zeroLogger.LogInformation($"Processing payment , {1000:@Id}");
        }
    }
}