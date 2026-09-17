using KetPsi.Extensions.Logging.Console;
using KetPsi.Extensions.Logging.OpenTelemetry;

using OpenTelemetry.Logs;

public partial class Program
{
    private static readonly PaymentPayload _payload = new()
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
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders().AddZeroAllocConsole().AddZeroAllocOtlp();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation($"This is the first log with {Guid.NewGuid():@Random:mask:..4}");
        logger.LogInformation($"This is the second log with {_payload:json}");

        await Task.Delay(5000);

        logger.LogInformation($"This is the third log with {Guid.NewGuid():@Random:mask:..4}");
        logger.LogInformation($"This is the forth log with {_payload:json}");


        app.Run();
    }
}

