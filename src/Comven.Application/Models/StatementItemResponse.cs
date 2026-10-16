using Comven.Domain.Enums;

namespace Comven.Application.Models;

public sealed record StatementItemResponse(
    Guid OperationId,
    DateTimeOffset CreatedAt,
    string Description,
    string Provider,
    OperationKind Kind,
    OperationStatus Status,
    decimal Amount,
    decimal BalanceAfter);