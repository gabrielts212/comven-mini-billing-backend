using Comven.Domain.Enums;

namespace Comven.Application.Models;

public sealed record CreateOperationResponse(
    Guid OperationId,
    OperationStatus Status,
    string Provider,
    decimal AvailableBalance,
    bool IdempotentReplay,
    string Message);