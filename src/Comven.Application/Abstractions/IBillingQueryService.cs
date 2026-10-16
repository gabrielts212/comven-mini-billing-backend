using Comven.Application.Models;

namespace Comven.Application.Abstractions;

public interface IBillingQueryService
{
    Task<StatementOverviewResponse> GetStatementAsync(StatementFilter filter, CancellationToken cancellationToken);
}