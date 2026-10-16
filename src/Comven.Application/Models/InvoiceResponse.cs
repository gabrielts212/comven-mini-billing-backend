using Comven.Domain.Enums;

namespace Comven.Application.Models;

public sealed record InvoiceResponse(
    Guid InvoiceId,
    string CustomerExternalId,
    string CustomerName,
    AccountType AccountType,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt,
    decimal TotalAmount,
    int OperationCount,
    string Status);