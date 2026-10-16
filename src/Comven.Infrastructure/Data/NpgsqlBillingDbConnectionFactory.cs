using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Comven.Infrastructure.Data;

public sealed class NpgsqlBillingDbConnectionFactory(IConfiguration configuration) : IBillingDbConnectionFactory
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Connection string 'Postgres' was not configured.");

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
