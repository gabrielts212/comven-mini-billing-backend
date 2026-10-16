using Comven.Domain.Enums;

namespace Comven.Application.Models;

public sealed record StatementOverviewResponse(
    string CustomerExternalId,
    string CustomerName,
    AccountType AccountType,
    decimal AvailableBalance,
    decimal OutstandingInvoiceAmount,
    IReadOnlyList<StatementItemResponse> Items);