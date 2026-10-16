using System.Data.Common;
using System.Text.Json;
using Comven.Application.Models;
using Comven.Domain.Enums;
using Comven.Infrastructure.Data;
using Comven.Infrastructure.Services;
using Dapper;
using Npgsql;

namespace Comven.Tests;

[Collection("Postgres integration")]
public sealed class PostgresBillingIntegrationTests(PostgresTestDatabaseFixture fixture)
{
    [Fact]
    public async Task CreateOperationAsync_ShouldFallbackToLaterProvider_WhenEarlierProvidersFail()
    {
        await fixture.ResetAsync();
        var service = fixture.CreateService();
        var request = new CreateOperationRequest(
            "cli-pre-001",
            50m,
            "Consulta com failover",
            OperationKind.Authorization,
            FindKeyForScenario(2));

        var result = await service.CreateOperationAsync(request, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal("Provider C", result.Provider);
        Assert.Equal(1450m, result.AvailableBalance);

        var providerAttempts = await fixture.QueryAsync<string>(
            """
            select provider_code
            from operation_audit_events
            where operation_id = @OperationId
              and event_type = 'provider_attempt';
            """,
            new { result.OperationId });

        Assert.Equal(3, providerAttempts.Count);
        Assert.Contains("provider-a", providerAttempts);
        Assert.Contains("provider-b", providerAttempts);
        Assert.Contains("provider-c", providerAttempts);
    }

    [Fact]
    public async Task CreateOperationAsync_ShouldApplySingleRefund_WhenAllProvidersFailForPrepaid()
    {
        await fixture.ResetAsync();
        var service = fixture.CreateService();
        var request = new CreateOperationRequest(
            "cli-pre-001",
            75m,
            "Consulta com falha total",
            OperationKind.Authorization,
            $"all-fail-{Guid.NewGuid():N}",
            SimulateProviderFailure: true);

        var result = await service.CreateOperationAsync(request, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(1500m, result.AvailableBalance);

        var ledgerEntries = await fixture.QueryAsync<decimal>(
            """
            select amount
            from ledger_entries
            where operation_id = @OperationId;
            """,
            new { result.OperationId });

        Assert.Equal(2, ledgerEntries.Count);
        Assert.Equal(0m, ledgerEntries.Sum());
        Assert.Contains(-75m, ledgerEntries);
        Assert.Contains(75m, ledgerEntries);

        var refundEvents = await fixture.QueryScalarAsync<int>(
            """
            select count(*)
            from operation_audit_events
            where operation_id = @OperationId
              and event_type = 'balance_refunded';
            """,
            new { result.OperationId });

        Assert.Equal(1, refundEvents);
    }

    [Fact]
    public async Task CreateOperationAsync_ShouldReplayExistingOperation_WhenIdempotencyKeyIsReused()
    {
        await fixture.ResetAsync();
        var service = fixture.CreateService();
        var request = new CreateOperationRequest(
            "cli-pre-001",
            20m,
            "Consulta idempotente",
            OperationKind.Authorization,
            $"idempotency-{Guid.NewGuid():N}");

        var first = await service.CreateOperationAsync(request, CancellationToken.None);
        var second = await service.CreateOperationAsync(request, CancellationToken.None);

        Assert.False(first.IdempotentReplay);
        Assert.True(second.IdempotentReplay);
        Assert.Equal(first.OperationId, second.OperationId);

        var operationCount = await fixture.QueryScalarAsync<int>(
            """
            select count(*)
            from billing_operations
            where idempotency_key = @IdempotencyKey;
            """,
            new { request.IdempotencyKey });

        Assert.Equal(1, operationCount);
    }

    [Fact]
    public async Task CreateOperationAsync_ShouldHandleConcurrentDuplicateRequests()
    {
        await fixture.ResetAsync();
        var request = new CreateOperationRequest(
            "cli-pre-001",
            35m,
            "Consulta concorrente",
            OperationKind.Authorization,
            $"concurrent-{Guid.NewGuid():N}");

        var firstTask = fixture.CreateService().CreateOperationAsync(request, CancellationToken.None);
        var secondTask = fixture.CreateService().CreateOperationAsync(request, CancellationToken.None);

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(results[0].OperationId, result.OperationId));
        Assert.Equal(1, results.Count(result => !result.IdempotentReplay));
        Assert.Equal(1, results.Count(result => result.IdempotentReplay));

        var operationCount = await fixture.QueryScalarAsync<int>(
            """
            select count(*)
            from billing_operations
            where idempotency_key = @IdempotencyKey;
            """,
            new { request.IdempotencyKey });

        Assert.Equal(1, operationCount);
    }

    [Fact]
    public async Task CloseOpenInvoicesAsync_ShouldNotDuplicateInvoices_WhenExecutedTwice()
    {
        await fixture.ResetAsync();
        var service = fixture.CreateService();

        var first = await service.CloseOpenInvoicesAsync(CancellationToken.None);
        var second = await service.CloseOpenInvoicesAsync(CancellationToken.None);

        Assert.Equal(1, first.ClosedInvoiceCount);
        Assert.Equal(0, second.ClosedInvoiceCount);

        var invoiceCount = await fixture.QueryScalarAsync<int>(
            """
            select count(*)
            from invoices
            where customer_external_id = 'cli-post-001';
            """,
            null);

        Assert.Equal(1, invoiceCount);
    }

    private static string FindKeyForScenario(int expectedIndex)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var candidate = $"scenario-{expectedIndex}-{index}";
            if (ComputeStableHash(candidate) % 3 == expectedIndex)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to find an idempotency key for the requested provider scenario.");
    }

    private static int ComputeStableHash(string value)
    {
        var hash = 0;
        foreach (var character in value)
        {
            hash = unchecked((hash * 31) + character);
        }

        return Math.Abs(hash);
    }
}

public sealed class PostgresTestDatabaseFixture : IAsyncLifetime
{
    private readonly TestDbConnectionFactory _connectionFactory;
    private readonly PostgresSchemaInitializer _schemaInitializer;

    public PostgresTestDatabaseFixture()
    {
        _connectionFactory = new TestDbConnectionFactory(ResolveConnectionString());
        _schemaInitializer = new PostgresSchemaInitializer(_connectionFactory);
    }

    public async Task InitializeAsync()
    {
        await _schemaInitializer.EnsureInitializedAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public DapperBillingService CreateService() => new(_connectionFactory);

    public async Task ResetAsync()
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            "truncate table operation_audit_events, ledger_entries, billing_operations, invoices, providers, customer_accounts restart identity cascade;");

        await _schemaInitializer.EnsureInitializedAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
        var results = await connection.QueryAsync<T>(sql, parameters);
        return results.ToList();
    }

    public async Task<T> QueryScalarAsync<T>(string sql, object? parameters)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }

    private static string ResolveConnectionString()
    {
        var overrideValue = Environment.GetEnvironmentVariable("COMVEN_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue;
        }

        var appSettingsPath = ResolveAppSettingsPath();
        using var stream = File.OpenRead(appSettingsPath);
        using var document = JsonDocument.Parse(stream);

        return document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("Postgres")
            .GetString()
            ?? throw new InvalidOperationException("Connection string 'Postgres' was not found for integration tests.");
    }

    private static string ResolveAppSettingsPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Comven.Api", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate src/Comven.Api/appsettings.json from the test output directory.");
    }

    private sealed class TestDbConnectionFactory(string connectionString) : IBillingDbConnectionFactory
    {
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
