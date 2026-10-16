using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Comven.Tests;

[CollectionDefinition("Postgres integration")]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresTestDatabaseFixture>
{
}
