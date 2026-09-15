using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using KetPsi.Extensions.Logging.OpenTelemetry.Internals;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Logs;

[MemoryDiagnoser]
public class OtlpLoggerBenchmarks
{
    private ILogger<OtlpLoggerBenchmarks> _msLogger = null!;
    private ILogger<OtlpLoggerBenchmarks> _zeroLogger = null!;

    // Parameters for Standard / Complex
    private readonly string _cardNumber = "4111222233334444";
    private readonly int _paymentId = 98765;

    // Parameters for the Extreme Benchmark
    private readonly Guid _txId = Guid.NewGuid();
    private readonly int _userId = 847291;
    private readonly string _ipAddress = "192.168.1.105";
    private readonly decimal _amount = 1500.75m;
    private readonly string _currency = "USD";
    private readonly string _status = "Authorized";
    private readonly int _retries = 2;
    private readonly string _gateway = "Stripe";
    private readonly double _latency = 145.2;

    private readonly PaymentPayload _payload = new()
    {
        Amount = 1500.75m,
        Currency = "USD",
        CardType = "Visa",
        FraudScore = 0.02
    };

    public class PaymentPayload
    {
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string CardType { get; set; } = string.Empty;
        public double FraudScore { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        Console.SetOut(TextWriter.Null);

        var msServices = new ServiceCollection();
        msServices.AddLogging(builder =>
        {
            builder.AddOpenTelemetry(otp =>
            {
                otp.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri("http://localhost:4318/v1/logs");
                    otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    otlp.HttpClientFactory = () => new HttpClient(new NoOpHttpHandler());
                });
            });
        });
        _msLogger = msServices.BuildServiceProvider().GetRequiredService<ILogger<OtlpLoggerBenchmarks>>();

        var zeroServices = new ServiceCollection();
        zeroServices.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.Services.AddSingleton<ILoggerProvider>(srv =>
                new ZeroAllocOtlpLoggerProvider(new HttpClient(new NoOpHttpHandler()), 1024, []));
        });
        _zeroLogger = zeroServices.BuildServiceProvider().GetRequiredService<ILogger<OtlpLoggerBenchmarks>>();
    }

    //[Benchmark(Baseline = true)]
    //public void Standard_LogInformation()
    //{
    //    int arg1 = 1234;
    //    string arg2 = "PaymentService";

    //    _msLogger.LogInformation("Start the process with id {Id} for {Service}", arg1, arg2);
    //}

    //[Benchmark]
    //public void Interpolated_LogInformation()
    //{
    //    int arg1 = 1234;
    //    string arg2 = "PaymentService";

    //    _zeroLogger.LogInformation($"Start the process with id {arg1} for {arg2}");
    //}

    //[Benchmark]
    //public void Standard_Complex_LogInformation()
    //{
    //    string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
    //    {
    //        card.AsSpan().CopyTo(span);
    //        span[..^4].Fill('*');
    //    });

    //    _msLogger.LogInformation("Processing payment {Id} for card {Card} with payload {Payload}",
    //        _paymentId, maskedCard, string.Empty);
    //}

    //[Benchmark]
    //public void Interpolated_Complex_LogInformation()
    //{
    //    _zeroLogger.LogInformation($"Processing payment {_paymentId} for card {_cardNumber:mask:..4}");
    //}

    [Benchmark]
    public void Standard_Extreme_LogInformation()
    {
        // Microsoft ILogger forces a manual JSON string allocation
        string json = JsonSerializer.Serialize(_payload);

        // Allocates object[10], boxes the Guid, int, decimal, int, and double.
        _msLogger.LogInformation(
            "Processing heavy transaction {TxId} for user {UserId} from IP {IpAddress}. " +
            "Amount: {Amount} {Currency}, Status: {Status}, Retries: {Retries}, " +
            "Gateway: {Gateway}, Latency: {Latency}ms. Payload: {Payload}",
            _txId, _userId, _ipAddress, _amount, _currency, _status, _retries, _gateway, _latency, json);

        _msLogger.LogInformation(
            "Processing heavy transaction {TxId} for user {UserId} from IP {IpAddress}. " +
            "Amount: {Amount} {Currency}, Status: {Status}, Retries: {Retries}, " +
            "Gateway: {Gateway}, Latency: {Latency}ms. Payload: {Payload}",
            _txId, _userId, _ipAddress, _amount, _currency, _status, _retries, _gateway, _latency, json);
    }

    [Benchmark]
    public void Interpolated_Extreme_LogInformation()
    {
        // KetPsi Source Generator intercepts this.
        _zeroLogger.LogInformation(
            $"Processing heavy transaction {_txId} for user {_userId} from IP {_ipAddress}. " +
            $"Amount: {_amount} {_currency}, Status: {_status}, Retries: {_retries}, " +
            $"Gateway: {_gateway}, Latency: {_latency}ms. Payload: {_payload:json}");

        _zeroLogger.LogInformation(
            $"Processing heavy transaction {_txId} for user {_userId} from IP {_ipAddress}. " +
            $"Amount: {_amount} {_currency}, Status: {_status}, Retries: {_retries}, " +
            $"Gateway: {_gateway}, Latency: {_latency}ms. Payload: {_payload:json}");
    }

    //[Benchmark]
    //public void Standard_Burst_LogInformation()
    //{
    //    // 1. Simple int boxing
    //    _msLogger.LogInformation("Received payment request for user {UserId}", _userId);

    //    // 2. Manual string allocation and array allocation
    //    string maskedCard = string.Create(_cardNumber.Length, _cardNumber, (span, card) =>
    //    {
    //        card.AsSpan().CopyTo(span);
    //        span[..^4].Fill('*');
    //    });
    //    _msLogger.LogInformation("Validating card {CardNumber}", maskedCard);

    //    // 3. Simple string reference
    //    _msLogger.LogInformation("Contacting gateway {Gateway}", _gateway);

    //    // 4. Multiple boxing (decimal)
    //    _msLogger.LogInformation("Payment {Status} for {Amount} {Currency}", _status, _amount, _currency);

    //    // 5. Guid boxing and double boxing
    //    _msLogger.LogInformation("Transaction {TxId} completed in {Latency}ms", _txId, _latency);
    //}

    //[Benchmark]
    //public void Interpolated_Burst_LogInformation()
    //{
    //    // 1. Direct UTF-8 integer write
    //    _zeroLogger.LogInformation($"Received payment request for user {_userId}");

    //    // 2. Source-generated masking, no string allocation
    //    _zeroLogger.LogInformation($"Validating card {_cardNumber:mask:..4}");

    //    // 3. Direct UTF-8 string copy
    //    _zeroLogger.LogInformation($"Contacting gateway {_gateway}");

    //    // 4. Direct UTF-8 decimal and string writes
    //    _zeroLogger.LogInformation($"Payment {_status} for {_amount} {_currency}");

    //    // 5. Direct UTF-8 Guid and double writes
    //    _zeroLogger.LogInformation($"Transaction {_txId} completed in {_latency}ms");
    //}
}

public class NoOpHttpHandler : HttpMessageHandler
{
    private static readonly HttpResponseMessage _successResponse = new(System.Net.HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[] { 0x0A, 0x00 })
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_successResponse);
    }
}