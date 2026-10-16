using Comven.Domain.Enums;

namespace Comven.Application.Models;

public sealed record CreateOperationRequest(
    string CustomerExternalId,
    decimal Amount,
    string Description,
    OperationKind Kind,
    string IdempotencyKey,
    bool SimulateProviderFailure = false);