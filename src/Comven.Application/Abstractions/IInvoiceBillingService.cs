using Comven.Application.Models;

namespace Comven.Application.Abstractions;

public interface IInvoiceBillingService
{
    Task<InvoiceCloseResponse> CloseOpenInvoicesAsync(CancellationToken cancellationToken);
}