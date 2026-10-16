namespace Comven.Application.Models;

public sealed record InvoiceCloseResponse(
    int ClosedInvoiceCount,
    IReadOnlyList<string> ReferenceMonths);