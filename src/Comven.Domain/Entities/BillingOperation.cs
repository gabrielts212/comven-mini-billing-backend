using Comven.Domain.Enums;

namespace Comven.Domain.Entities;

public sealed record BillingOperation(
    Guid Id,
    string IdempotencyKey,
    string CustomerExternalId,
    string Provider,
    string Description,
    OperationKind Kind,
    OperationStatus Status,
    decimal Amount,
    DateTimeOffset CreatedAt,
    string? FailureReason = null,
    Guid? InvoiceId = null);