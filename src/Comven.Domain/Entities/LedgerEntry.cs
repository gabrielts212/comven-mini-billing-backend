namespace Comven.Domain.Entities;

public sealed record LedgerEntry(
    Guid Id,
    string CustomerExternalId,
    Guid OperationId,
    string Description,
    decimal Amount,
    decimal BalanceAfter,
    DateTimeOffset CreatedAt);