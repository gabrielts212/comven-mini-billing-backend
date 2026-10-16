using Comven.Application.Models;

namespace Comven.Application.Abstractions;

public interface IInvoiceBillingService
{
    Task<IReadOnlyList<InvoiceResponse>> GetInvoicesAsync(InvoiceFilter filter, CancellationToken cancellationToken);
    Task<InvoiceCloseResponse> CloseOpenInvoicesAsync(CancellationToken cancellationToken);
}