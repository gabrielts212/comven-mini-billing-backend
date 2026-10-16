using Dapper;
using Comven.Application.Abstractions;
using Comven.Application.Models;
using Comven.Domain.Entities;
using Comven.Domain.Enums;
using Comven.Infrastructure.Data;

namespace Comven.Infrastructure.Services;

public sealed class DapperBillingService(IBillingDbConnectionFactory connectionFactory) : IBillingQueryService, IBillingCommandService, IInvoiceBillingService
{
    private static readonly string[] Providers = ["Provider A", "Provider B", "Provider C"];

    public async Task<StatementOverviewResponse> GetStatementAsync(StatementFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var customer = await connection.QuerySingleOrDefaultAsync<CustomerRow>(
            new CommandDefinition(
                """
                select
                    external_id as ExternalId,
                    name as Name,
                    account_type as AccountType,
                    available_balance as AvailableBalance
                from customer_accounts
                where external_id = @CustomerExternalId;
                """,
                new { filter.CustomerExternalId },
                cancellationToken: cancellationToken));

        if (customer is null)
        {
            throw new KeyNotFoundException($"Customer '{filter.CustomerExternalId}' was not found.");
        }

        var items = (await connection.QueryAsync<StatementItemRow>(
            new CommandDefinition(
                """
                select
                    operation_id as OperationId,
                    created_at as CreatedAt,
                    description as Description,
                    provider as Provider,
                    kind as Kind,
                    status as Status,
                    amount as Amount,
                    balance_after as BalanceAfter
                from billing_statement_items
                where customer_external_id = @CustomerExternalId
                  and (@From::date is null or created_on >= @From::date)
                  and (@To::date is null or created_on <= @To::date)
                order by created_at desc;
                """,
                new
                {
                    CustomerExternalId = customer.ExternalId,
                    filter.From,
                    filter.To
                },
                cancellationToken: cancellationToken))).ToList();

        var outstanding = customer.AccountType == AccountType.Postpaid
            ? await connection.ExecuteScalarAsync<decimal>(
                new CommandDefinition(
                    """
                    select coalesce(sum(amount), 0)
                    from billing_operations
                    where customer_external_id = @CustomerExternalId
                      and invoice_id is null
                      and status = @SucceededStatus;
                    """,
                    new
                    {
                        CustomerExternalId = customer.ExternalId,
                        SucceededStatus = OperationStatus.Succeeded
                    },
                    cancellationToken: cancellationToken))
            : 0m;

        return new StatementOverviewResponse(
            customer.ExternalId,
            customer.Name,
            customer.AccountType,
            customer.AvailableBalance,
            outstanding,
            items.Select(item => new StatementItemResponse(
                item.OperationId,
                item.CreatedAt.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc))
                    : new DateTimeOffset(item.CreatedAt),
                item.Description,
                item.Provider,
                (OperationKind)item.Kind,
                (OperationStatus)item.Status,
                item.Amount,
                item.BalanceAfter)).ToList());
    }

    public async Task<CreateOperationResponse> CreateOperationAsync(CreateOperationRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await connection.QuerySingleOrDefaultAsync<OperationReplayRow>(
            new CommandDefinition(
                """
                select
                    operation_id as OperationId,
                    status as Status,
                    provider as Provider,
                    message as Message,
                    available_balance as AvailableBalance
                from billing_operation_replays
                where idempotency_key = @IdempotencyKey;
                """,
                new { request.IdempotencyKey },
                transaction,
                cancellationToken: cancellationToken));

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CreateOperationResponse(
                existing.OperationId,
                existing.Status,
                existing.Provider,
                existing.AvailableBalance,
                true,
                existing.Message);
        }

        var customer = await connection.QuerySingleOrDefaultAsync<CustomerForUpdateRow>(
            new CommandDefinition(
                """
                select
                    id as Id,
                    external_id as ExternalId,
                    account_type as AccountType,
                    available_balance as AvailableBalance
                from customer_accounts
                where external_id = @CustomerExternalId
                for update;
                """,
                new { request.CustomerExternalId },
                transaction,
                cancellationToken: cancellationToken));

        if (customer is null)
        {
            throw new KeyNotFoundException($"Customer '{request.CustomerExternalId}' was not found.");
        }

        var provider = ResolveProvider(request.IdempotencyKey);
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        if (customer.AccountType == AccountType.Prepaid && customer.AvailableBalance < request.Amount)
        {
            const string deniedMessage = "Saldo insuficiente para operação pré-paga.";

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    insert into billing_operations (
                        id,
                        idempotency_key,
                        customer_external_id,
                        provider,
                        description,
                        kind,
                        status,
                        amount,
                        created_at,
                        response_message,
                        failure_reason)
                    values (
                        @Id,
                        @IdempotencyKey,
                        @CustomerExternalId,
                        @Provider,
                        @Description,
                        @Kind,
                        @Status,
                        @Amount,
                        @CreatedAt,
                        @ResponseMessage,
                        @FailureReason);
                    """,
                    new
                    {
                        Id = operationId,
                        request.IdempotencyKey,
                        CustomerExternalId = customer.ExternalId,
                        Provider = provider,
                        request.Description,
                        request.Kind,
                        Status = OperationStatus.Failed,
                        request.Amount,
                        CreatedAt = now,
                        ResponseMessage = deniedMessage,
                        FailureReason = deniedMessage
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return new CreateOperationResponse(operationId, OperationStatus.Failed, provider, customer.AvailableBalance, false, deniedMessage);
        }

        var balanceAfter = customer.AvailableBalance;
        if (customer.AccountType == AccountType.Prepaid)
        {
            balanceAfter -= request.Amount;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    update customer_accounts
                    set available_balance = @AvailableBalance
                    where id = @CustomerId;
                    """,
                    new
                    {
                        AvailableBalance = balanceAfter,
                        CustomerId = customer.Id
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        var status = request.SimulateProviderFailure ? OperationStatus.Refunded : OperationStatus.Succeeded;
        var message = request.SimulateProviderFailure
            ? "Todos os fornecedores falharam; estorno aplicado uma única vez."
            : "Operação registrada com sucesso.";
        var failureReason = request.SimulateProviderFailure ? "Provider timeout" : null;
        var ledgerAmount = customer.AccountType == AccountType.Prepaid ? -request.Amount : request.Amount;

        if (request.SimulateProviderFailure && customer.AccountType == AccountType.Prepaid)
        {
            balanceAfter = customer.AvailableBalance;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    update customer_accounts
                    set available_balance = @AvailableBalance
                    where id = @CustomerId;
                    """,
                    new
                    {
                        AvailableBalance = balanceAfter,
                        CustomerId = customer.Id
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            ledgerAmount = 0m;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                insert into billing_operations (
                    id,
                    idempotency_key,
                    customer_external_id,
                    provider,
                    description,
                    kind,
                    status,
                    amount,
                    created_at,
                    response_message,
                    failure_reason)
                values (
                    @Id,
                    @IdempotencyKey,
                    @CustomerExternalId,
                    @Provider,
                    @Description,
                    @Kind,
                    @Status,
                    @Amount,
                    @CreatedAt,
                    @ResponseMessage,
                    @FailureReason);
                """,
                new
                {
                    Id = operationId,
                    request.IdempotencyKey,
                    CustomerExternalId = customer.ExternalId,
                    Provider = provider,
                    request.Description,
                    request.Kind,
                    Status = status,
                    request.Amount,
                    CreatedAt = now,
                    ResponseMessage = message,
                    FailureReason = failureReason
                },
                transaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                insert into ledger_entries (
                    id,
                    customer_external_id,
                    operation_id,
                    description,
                    amount,
                    balance_after,
                    created_at)
                values (
                    @Id,
                    @CustomerExternalId,
                    @OperationId,
                    @Description,
                    @Amount,
                    @BalanceAfter,
                    @CreatedAt);
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    CustomerExternalId = customer.ExternalId,
                    OperationId = operationId,
                    request.Description,
                    Amount = ledgerAmount,
                    BalanceAfter = balanceAfter,
                    CreatedAt = now
                },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new CreateOperationResponse(operationId, status, provider, balanceAfter, false, message);
    }

    public async Task<InvoiceCloseResponse> CloseOpenInvoicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var referenceMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var createdMonths = new List<string>();

        var customers = await connection.QueryAsync<CustomerTypeRow>(
            new CommandDefinition(
                """
                select external_id as ExternalId
                from customer_accounts
                where account_type = @AccountType
                order by external_id;
                """,
                new { AccountType = AccountType.Postpaid },
                transaction,
                cancellationToken: cancellationToken));

        foreach (var customer in customers)
        {
            var alreadyClosed = await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    select exists(
                        select 1
                        from invoices
                        where customer_external_id = @CustomerExternalId
                          and reference_month = @ReferenceMonth);
                    """,
                    new
                    {
                        CustomerExternalId = customer.ExternalId,
                        ReferenceMonth = referenceMonth
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            if (alreadyClosed)
            {
                continue;
            }

            var pendingOperationIds = (await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    select id
                    from billing_operations
                    where customer_external_id = @CustomerExternalId
                      and invoice_id is null
                      and status = @SucceededStatus
                    for update;
                    """,
                    new
                    {
                        CustomerExternalId = customer.ExternalId,
                        SucceededStatus = OperationStatus.Succeeded
                    },
                    transaction,
                    cancellationToken: cancellationToken))).ToList();

            if (pendingOperationIds.Count == 0)
            {
                continue;
            }

            var amount = await connection.ExecuteScalarAsync<decimal>(
                new CommandDefinition(
                    """
                    select coalesce(sum(amount), 0)
                    from billing_operations
                    where id = any(@OperationIds);
                    """,
                    new { OperationIds = pendingOperationIds.ToArray() },
                    transaction,
                    cancellationToken: cancellationToken));

            var invoiceId = Guid.NewGuid();
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    insert into invoices (
                        id,
                        customer_external_id,
                        reference_month,
                        amount,
                        closed_at,
                        operation_count)
                    values (
                        @Id,
                        @CustomerExternalId,
                        @ReferenceMonth,
                        @Amount,
                        @ClosedAt,
                        @OperationCount);
                    """,
                    new
                    {
                        Id = invoiceId,
                        CustomerExternalId = customer.ExternalId,
                        ReferenceMonth = referenceMonth,
                        Amount = amount,
                        ClosedAt = DateTimeOffset.UtcNow,
                        OperationCount = pendingOperationIds.Count
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    update billing_operations
                    set invoice_id = @InvoiceId
                    where id = any(@OperationIds);
                    """,
                    new
                    {
                        InvoiceId = invoiceId,
                        OperationIds = pendingOperationIds.ToArray()
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            createdMonths.Add(referenceMonth);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InvoiceCloseResponse(createdMonths.Count, createdMonths);
    }

    private static string ResolveProvider(string idempotencyKey)
    {
        var hash = 0;
        foreach (var character in idempotencyKey)
        {
            hash = unchecked((hash * 31) + character);
        }

        var index = Math.Abs(hash) % Providers.Length;
        return Providers[index];
    }

    private sealed record CustomerRow(string ExternalId, string Name, AccountType AccountType, decimal AvailableBalance);

    private sealed record StatementItemRow(
        Guid OperationId,
        DateTime CreatedAt,
        string Description,
        string Provider,
        int Kind,
        int Status,
        decimal Amount,
        decimal BalanceAfter);

    private sealed record OperationReplayRow(
        Guid OperationId,
        OperationStatus Status,
        string Provider,
        decimal AvailableBalance,
        string Message);

    private sealed record CustomerForUpdateRow(Guid Id, string ExternalId, AccountType AccountType, decimal AvailableBalance);

    private sealed record CustomerTypeRow(string ExternalId);
}
