namespace Comven.Domain.Entities;

public sealed record Invoice(
    Guid Id,
    string CustomerExternalId,
    string ReferenceMonth,
    decimal Amount,
    DateTimeOffset ClosedAt,
    int OperationCount);