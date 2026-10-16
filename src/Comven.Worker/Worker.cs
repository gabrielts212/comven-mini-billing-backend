using Comven.Application.Abstractions;

namespace Comven.Worker;

public sealed class Worker(
    ILogger<Worker> logger,
    IInvoiceBillingService invoiceBillingService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await invoiceBillingService.CloseOpenInvoicesAsync(stoppingToken);
            logger.LogInformation(
                "Invoice cycle executed at {time}. Closed invoices: {count}",
                DateTimeOffset.UtcNow,
                result.ClosedInvoiceCount);

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
