using Dapper;

namespace Comven.Infrastructure.Data;

public sealed class PostgresSchemaInitializer(IBillingDbConnectionFactory connectionFactory)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var scriptPath = ResolveInitScriptPath();
        var sql = await File.ReadAllTextAsync(scriptPath, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static string ResolveInitScriptPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "database", "init.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate database/init.sql from the current application directory.");
    }
}
