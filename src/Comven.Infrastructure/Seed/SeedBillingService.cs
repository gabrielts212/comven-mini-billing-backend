using Comven.Application.Abstractions;
using Comven.Application.Models;
using Comven.Domain.Entities;
using Comven.Domain.Enums;

namespace Comven.Infrastructure.Seed;

public sealed class SeedBillingService(SeedBillingStore store) : IBillingQueryService, IBillingCommandService, IInvoiceBillingService
{
    private static readonly string[] Providers = ["Provider A", "Provider B", "Provider C"];

    public Task<StatementOverviewResponse> GetStatementAsync(StatementFilter filter, CancellationToken cancellationToken)
    {
        var response = store.WithLock(state =>
        {
            if (!state.Customers.TryGetValue(filter.CustomerExternalId, out var customer))
            {
                throw new KeyNotFoundException($"Customer '{filter.CustomerExternalId}' was not found.");
            }

            var items = state.LedgerEntries
                .Where(entry => entry.CustomerExternalId.Equals(customer.ExternalId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => filter.From is null || DateOnly.FromDateTime(entry.CreatedAt.UtcDateTime) >= filter.From)
                .Where(entry => filter.To is null || DateOnly.FromDateTime(entry.CreatedAt.UtcDateTime) <= filter.To)
                .OrderByDescending(entry => entry.CreatedAt)
                .Join(
                    state.Operations,
                    entry => entry.OperationId,
                    operation => operation.Id,
                    (entry, operation) => new StatementItemResponse(
                        operation.Id,
                        entry.CreatedAt,
                        entry.Description,
                        operation.Provider,
                        operation.Kind,
                        operation.Status,
                        entry.Amount,
                        entry.BalanceAfter))
                .ToList();

            var outstanding = customer.AccountType == AccountType.Postpaid
                ? state.Operations
                    .Where(operation => operation.CustomerExternalId.Equals(customer.ExternalId, StringComparison.OrdinalIgnoreCase))
                    .Where(operation => operation.InvoiceId is null && operation.Status == OperationStatus.Succeeded)
                    .Sum(operation => operation.Amount)
                : 0m;

            return new StatementOverviewResponse(
                customer.ExternalId,
                customer.Name,
                customer.AccountType,
                customer.AvailableBalance,
                outstanding,
                items);
        });

        return Task.FromResult(response);
    }

    public Task<CreateOperationResponse> CreateOperationAsync(CreateOperationRequest request, CancellationToken cancellationToken)
    {
        var response = store.WithLock(state =>
        {
            if (state.IdempotencyIndex.TryGetValue(request.IdempotencyKey, out var existing))
            {
                return new CreateOperationResponse(
                    existing.OperationId,
                    existing.Status,
                    existing.Provider,
                    existing.BalanceAfter,
                    true,
                    existing.Message);
            }

            if (!state.Customers.TryGetValue(request.CustomerExternalId, out var customer))
            {
                throw new KeyNotFoundException($"Customer '{request.CustomerExternalId}' was not found.");
            }

            var provider = ResolveProvider(request.IdempotencyKey);
            var operationId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            if (customer.AccountType == AccountType.Prepaid && !customer.CanDebit(request.Amount))
            {
                var denied = new CreateOperationResponse(
                    operationId,
                    OperationStatus.Failed,
                    provider,
                    customer.AvailableBalance,
                    false,
                    "Saldo insuficiente para operação pré-paga.");

                state.IdempotencyIndex[request.IdempotencyKey] = new SeedBillingStore.CreateOperationSeedSnapshot(
                    denied.OperationId,
                    customer.ExternalId,
                    denied.AvailableBalance,
                    denied.Status,
                    denied.Provider,
                    denied.Message);

                return denied;
            }

            if (customer.AccountType == AccountType.Prepaid)
            {
                customer.Debit(request.Amount);
            }

            var status = request.SimulateProviderFailure ? OperationStatus.Refunded : OperationStatus.Succeeded;
            var signedAmount = customer.AccountType == AccountType.Prepaid ? -request.Amount : request.Amount;
            var message = request.SimulateProviderFailure
                ? "Todos os fornecedores falharam; estorno aplicado uma única vez."
                : "Operação registrada com sucesso.";

            if (request.SimulateProviderFailure && customer.AccountType == AccountType.Prepaid)
            {
                customer.Credit(request.Amount);
            }

            var balanceAfter = customer.AvailableBalance;
            var operation = new BillingOperation(
                operationId,
                request.IdempotencyKey,
                customer.ExternalId,
                provider,
                request.Description,
                request.Kind,
                status,
                request.Amount,
                now,
                request.SimulateProviderFailure ? "Provider timeout" : null);

            state.Operations.Add(operation);
            state.LedgerEntries.Add(new LedgerEntry(
                Guid.NewGuid(),
                customer.ExternalId,
                operationId,
                request.Description,
                request.SimulateProviderFailure && customer.AccountType == AccountType.Prepaid ? 0m : signedAmount,
                balanceAfter,
                now));

            var result = new CreateOperationResponse(operationId, status, provider, balanceAfter, false, message);
            state.IdempotencyIndex[request.IdempotencyKey] = new SeedBillingStore.CreateOperationSeedSnapshot(
                result.OperationId,
                customer.ExternalId,
                result.AvailableBalance,
                result.Status,
                result.Provider,
                result.Message);

            return result;
        });

        return Task.FromResult(response);
    }

    public Task<InvoiceCloseResponse> CloseOpenInvoicesAsync(CancellationToken cancellationToken)
    {
        var response = store.WithLock(state =>
        {
            var referenceMonth = DateTime.UtcNow.ToString("yyyy-MM");
            var createdMonths = new List<string>();

            foreach (var customer in state.Customers.Values.Where(customer => customer.AccountType == AccountType.Postpaid))
            {
                var alreadyClosed = state.Invoices.Any(invoice =>
                    invoice.CustomerExternalId.Equals(customer.ExternalId, StringComparison.OrdinalIgnoreCase) &&
                    invoice.ReferenceMonth == referenceMonth);

                if (alreadyClosed)
                {
                    continue;
                }

                var pendingOperations = state.Operations
                    .Where(operation => operation.CustomerExternalId.Equals(customer.ExternalId, StringComparison.OrdinalIgnoreCase))
                    .Where(operation => operation.InvoiceId is null && operation.Status == OperationStatus.Succeeded)
                    .ToList();

                if (pendingOperations.Count == 0)
                {
                    continue;
                }

                var invoiceId = Guid.NewGuid();
                var amount = pendingOperations.Sum(operation => operation.Amount);
                state.Invoices.Add(new Invoice(invoiceId, customer.ExternalId, referenceMonth, amount, DateTimeOffset.UtcNow, pendingOperations.Count));

                for (var index = 0; index < state.Operations.Count; index++)
                {
                    var current = state.Operations[index];
                    if (pendingOperations.Any(operation => operation.Id == current.Id))
                    {
                        state.Operations[index] = current with { InvoiceId = invoiceId };
                    }
                }

                createdMonths.Add(referenceMonth);
            }

            return new InvoiceCloseResponse(createdMonths.Count, createdMonths);
        });

        return Task.FromResult(response);
    }

    private static string ResolveProvider(string idempotencyKey)
    {
        var index = Math.Abs(idempotencyKey.GetHashCode()) % Providers.Length;
        return Providers[index];
    }
}