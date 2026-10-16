using System.Data.Common;

namespace Comven.Infrastructure.Data;

public interface IBillingDbConnectionFactory
{
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
