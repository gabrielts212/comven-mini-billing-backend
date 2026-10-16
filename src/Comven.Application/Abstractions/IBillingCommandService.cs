using Comven.Application.Models;

namespace Comven.Application.Abstractions;

public interface IBillingCommandService
{
    Task<CreateOperationResponse> CreateOperationAsync(CreateOperationRequest request, CancellationToken cancellationToken);
}