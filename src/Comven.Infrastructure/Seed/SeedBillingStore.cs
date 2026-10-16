using Comven.Domain.Entities;
using Comven.Domain.Enums;

namespace Comven.Infrastructure.Seed;

public sealed class SeedBillingStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CustomerAccount> _customers;
    private readonly List<BillingOperation> _operations;
    private readonly List<LedgerEntry> _ledgerEntries;
    private readonly Dictionary<string, CreateOperationSeedSnapshot> _idempotencyIndex;
    private readonly List<Invoice> _invoices;

    public SeedBillingStore()
    {
        var now = DateTimeOffset.UtcNow;
        var prepaid = new CustomerAccount(
            Guid.NewGuid(),
            "cli-pre-001",
            "Ada Lovelace",
            AccountType.Prepaid,
            1500m,
            new DateOnly(now.Year, now.Month, 5));

        var postpaid = new CustomerAccount(
            Guid.NewGuid(),
            "cli-post-001",
            "Grace Hopper",
            AccountType.Postpaid,
            0m,
            new DateOnly(now.Year, now.Month, 15));

        _customers = new Dictionary<string, CustomerAccount>(StringComparer.OrdinalIgnoreCase)
        {
            [prepaid.ExternalId] = prepaid,
            [postpaid.ExternalId] = postpaid
        };

        _operations =
        [
            new BillingOperation(Guid.NewGuid(), "seed-pre-1", prepaid.ExternalId, "Provider A", "Recarga inicial", OperationKind.Authorization, OperationStatus.Succeeded, 500m, now.AddDays(-4)),
            new BillingOperation(Guid.NewGuid(), "seed-pre-2", prepaid.ExternalId, "Provider B", "Pagamento de boleto", OperationKind.Authorization, OperationStatus.Succeeded, 120m, now.AddDays(-2)),
            new BillingOperation(Guid.NewGuid(), "seed-post-1", postpaid.ExternalId, "Provider C", "Compra pós-paga", OperationKind.Authorization, OperationStatus.Succeeded, 320m, now.AddDays(-3))
        ];

        _ledgerEntries =
        [
            new LedgerEntry(Guid.NewGuid(), prepaid.ExternalId, _operations[0].Id, "Recarga inicial", 500m, 1620m, now.AddDays(-4)),
            new LedgerEntry(Guid.NewGuid(), prepaid.ExternalId, _operations[1].Id, "Pagamento de boleto", -120m, 1500m, now.AddDays(-2)),
            new LedgerEntry(Guid.NewGuid(), postpaid.ExternalId, _operations[2].Id, "Compra pós-paga", 320m, 320m, now.AddDays(-3))
        ];

        _idempotencyIndex = new Dictionary<string, CreateOperationSeedSnapshot>(StringComparer.OrdinalIgnoreCase);
        _invoices = [];
    }

    public T WithLock<T>(Func<SeedBillingState, T> action)
    {
        lock (_lock)
        {
            return action(new SeedBillingState(_customers, _operations, _ledgerEntries, _idempotencyIndex, _invoices));
        }
    }

    public sealed record CreateOperationSeedSnapshot(Guid OperationId, string CustomerExternalId, decimal BalanceAfter, OperationStatus Status, string Provider, string Message);

    public sealed record SeedBillingState(
        Dictionary<string, CustomerAccount> Customers,
        List<BillingOperation> Operations,
        List<LedgerEntry> LedgerEntries,
        Dictionary<string, CreateOperationSeedSnapshot> IdempotencyIndex,
        List<Invoice> Invoices);
}