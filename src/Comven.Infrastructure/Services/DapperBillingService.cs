using Dapper;
using Comven.Application.Abstractions;
using Comven.Application.Models;
using Comven.Domain.Entities;
using Comven.Domain.Enums;
using Comven.Infrastructure.Data;
using Npgsql;
using System.Globalization;

namespace Comven.Infrastructure.Services;

public sealed class DapperBillingService(IBillingDbConnectionFactory connectionFactory) : IBillingQueryService, IBillingCommandService, IInvoiceBillingService
{
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
        try
        {
            var existing = await TryGetExistingOperationAsync(connection, transaction, request.IdempotencyKey, cancellationToken);
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

            var providers = await GetActiveProvidersAsync(connection, transaction, cancellationToken);
            if (providers.Count == 0)
            {
                throw new InvalidOperationException("No active providers are configured.");
            }

            var operationId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var auditEvents = new List<AuditEventRow>
            {
                CreateAuditEvent(
                    operationId,
                    customer.ExternalId,
                    request.IdempotencyKey,
                    "operation_received",
                    null,
                    $"Operação recebida para {request.Description} no valor de {request.Amount:0.00}.",
                    now,
                    $"{{\"kind\":{(int)request.Kind},\"simulateProviderFailure\":{ToJsonBoolean(request.SimulateProviderFailure)}}}")
            };

            if (customer.AccountType == AccountType.Prepaid && customer.AvailableBalance < request.Amount)
            {
                const string deniedMessage = "Saldo insuficiente para operação pré-paga.";

                await InsertOperationAsync(
                    connection,
                    transaction,
                    new OperationInsertRow(
                        operationId,
                        request.IdempotencyKey,
                        customer.ExternalId,
                        providers[0].Name,
                        request.Description,
                        request.Kind,
                        OperationStatus.Failed,
                        request.Amount,
                    customer.AvailableBalance,
                        now,
                        deniedMessage,
                        deniedMessage),
                    cancellationToken);

                auditEvents.Add(CreateAuditEvent(
                    operationId,
                    customer.ExternalId,
                    request.IdempotencyKey,
                    "operation_rejected",
                    null,
                    deniedMessage,
                    now,
                    $"{{\"reason\":\"insufficient_balance\",\"availableBalance\":{ToJsonNumber(customer.AvailableBalance)}}}"));

                await InsertAuditEventsAsync(connection, transaction, auditEvents, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new CreateOperationResponse(operationId, OperationStatus.Failed, providers[0].Name, customer.AvailableBalance, false, deniedMessage);
            }

            var ledgerEntries = new List<LedgerInsertRow>();
            var balanceAfter = customer.AvailableBalance;

            if (customer.AccountType == AccountType.Prepaid)
            {
                balanceAfter -= request.Amount;
                await UpdateCustomerBalanceAsync(connection, transaction, customer.Id, balanceAfter, cancellationToken);

                ledgerEntries.Add(new LedgerInsertRow(
                    Guid.NewGuid(),
                    customer.ExternalId,
                    operationId,
                    request.Description,
                    -request.Amount,
                    balanceAfter,
                    now));

                auditEvents.Add(CreateAuditEvent(
                    operationId,
                    customer.ExternalId,
                    request.IdempotencyKey,
                    "balance_debited",
                    null,
                    $"Saldo debitado em {request.Amount:0.00} para cliente pré-pago.",
                    now,
                    $"{{\"balanceAfter\":{ToJsonNumber(balanceAfter)}}}"));
            }

            var providerExecution = SimulateProviderExecution(providers, request);
            foreach (var attempt in providerExecution.Attempts)
            {
                auditEvents.Add(CreateAuditEvent(
                    operationId,
                    customer.ExternalId,
                    request.IdempotencyKey,
                    "provider_attempt",
                    attempt.Provider.Code,
                    attempt.Message,
                    now,
                    $"{{\"providerName\":\"{attempt.Provider.Name}\",\"outcome\":\"{attempt.Outcome}\",\"priority\":{attempt.Provider.Priority}}}"));
            }

            var finalProvider = providerExecution.Attempts[^1].Provider;
            var status = providerExecution.Succeeded ? OperationStatus.Succeeded : OperationStatus.Failed;
            var message = providerExecution.Succeeded
                ? $"Operação registrada com sucesso via {providerExecution.SuccessfulProvider!.Name}."
                : "Todos os fornecedores falharam; operação cancelada.";
            var failureReason = providerExecution.Succeeded ? null : providerExecution.Attempts[^1].FailureReason;

            if (providerExecution.Succeeded && customer.AccountType == AccountType.Postpaid)
            {
                var outstanding = await GetOutstandingAmountAsync(connection, transaction, customer.ExternalId, cancellationToken);
                balanceAfter = outstanding + request.Amount;

                ledgerEntries.Add(new LedgerInsertRow(
                    Guid.NewGuid(),
                    customer.ExternalId,
                    operationId,
                    request.Description,
                    request.Amount,
                    balanceAfter,
                    now));
            }

            if (!providerExecution.Succeeded && customer.AccountType == AccountType.Prepaid)
            {
                balanceAfter = customer.AvailableBalance;
                await UpdateCustomerBalanceAsync(connection, transaction, customer.Id, balanceAfter, cancellationToken);

                ledgerEntries.Add(new LedgerInsertRow(
                    Guid.NewGuid(),
                    customer.ExternalId,
                    operationId,
                    $"Estorno: {request.Description}",
                    request.Amount,
                    balanceAfter,
                    now));

                auditEvents.Add(CreateAuditEvent(
                    operationId,
                    customer.ExternalId,
                    request.IdempotencyKey,
                    "balance_refunded",
                    finalProvider.Code,
                    $"Estorno aplicado em {request.Amount:0.00} após falha de todos os fornecedores.",
                    now,
                    $"{{\"balanceAfter\":{ToJsonNumber(balanceAfter)}}}"));
            }

            await InsertOperationAsync(
                connection,
                transaction,
                new OperationInsertRow(
                    operationId,
                    request.IdempotencyKey,
                    customer.ExternalId,
                    providerExecution.Succeeded ? providerExecution.SuccessfulProvider!.Name : finalProvider.Name,
                    request.Description,
                    request.Kind,
                    status,
                    request.Amount,
                    balanceAfter,
                    now,
                    message,
                    failureReason),
                cancellationToken);

            if (ledgerEntries.Count > 0)
            {
                await InsertLedgerEntriesAsync(connection, transaction, ledgerEntries, cancellationToken);
            }

            auditEvents.Add(CreateAuditEvent(
                operationId,
                customer.ExternalId,
                request.IdempotencyKey,
                providerExecution.Succeeded ? "operation_succeeded" : "operation_failed",
                providerExecution.Succeeded ? providerExecution.SuccessfulProvider!.Code : finalProvider.Code,
                message,
                now,
                $"{{\"status\":\"{status}\"}}"));

            await InsertAuditEventsAsync(connection, transaction, auditEvents, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var responseBalance = customer.AccountType == AccountType.Postpaid ? customer.AvailableBalance : balanceAfter;
            var responseProvider = providerExecution.Succeeded ? providerExecution.SuccessfulProvider!.Name : finalProvider.Name;
            return new CreateOperationResponse(operationId, status, responseProvider, responseBalance, false, message);
        }
        catch (PostgresException ex) when (IsIdempotencyConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);

            var replay = await TryGetExistingOperationAsync(connection, transaction: null, request.IdempotencyKey, cancellationToken);
            if (replay is not null)
            {
                return new CreateOperationResponse(
                    replay.OperationId,
                    replay.Status,
                    replay.Provider,
                    replay.AvailableBalance,
                    true,
                    replay.Message);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<InvoiceResponse>> GetInvoicesAsync(InvoiceFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<InvoiceRow>(
            new CommandDefinition(
                """
                select
                    i.id as InvoiceId,
                    ca.external_id as CustomerExternalId,
                    ca.name as CustomerName,
                    ca.account_type as AccountType,
                    i.reference_month as ReferenceMonth,
                    i.closed_at as GeneratedAt,
                    i.amount as TotalAmount,
                    i.operation_count as OperationCount
                from invoices i
                join customer_accounts ca on ca.external_id = i.customer_external_id
                where (@CustomerExternalId is null or i.customer_external_id = @CustomerExternalId)
                  and (@From::date is null or to_date(i.reference_month || '-01', 'YYYY-MM-DD') >= @From::date)
                  and (@To::date is null or (date_trunc('month', to_date(i.reference_month || '-01', 'YYYY-MM-DD')::timestamp) + interval '1 month - 1 day')::date <= @To::date)
                order by i.closed_at desc, i.id desc;
                """,
                new
                {
                    filter.CustomerExternalId,
                    filter.From,
                    filter.To
                },
                cancellationToken: cancellationToken))).ToList();

        return rows.Select(MapInvoiceResponse).ToList();
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

    private static async Task<OperationReplayRow?> TryGetExistingOperationAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleOrDefaultAsync<OperationReplayRow>(
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
                new { IdempotencyKey = idempotencyKey },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<ProviderRow>> GetActiveProvidersAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var providers = await connection.QueryAsync<ProviderRow>(
            new CommandDefinition(
                """
                select
                    code as Code,
                    name as Name,
                    priority as Priority
                from providers
                where is_active = true
                order by priority;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));

        return providers.ToList();
    }

    private static ProviderExecutionResult SimulateProviderExecution(
        IReadOnlyList<ProviderRow> providers,
        CreateOperationRequest request)
    {
        var attempts = new List<ProviderAttemptResult>();

        var targetSuccessIndex = request.SimulateProviderFailure
            ? -1
            : Math.Min(ComputeStableHash(request.IdempotencyKey) % providers.Count, providers.Count - 1);

        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];

            if (index == targetSuccessIndex)
            {
                attempts.Add(new ProviderAttemptResult(
                    provider,
                    ProviderAttemptOutcome.Succeeded,
                    $"{provider.Name} concluiu a operação com sucesso.",
                    null));

                return new ProviderExecutionResult(true, provider, attempts);
            }

            var outcome = index % 2 == 0
                ? ProviderAttemptOutcome.Failed
                : ProviderAttemptOutcome.TimedOut;
            var failureReason = outcome == ProviderAttemptOutcome.TimedOut
                ? "Provider timeout"
                : "Provider unavailable";
            var message = outcome == ProviderAttemptOutcome.TimedOut
                ? $"{provider.Name} excedeu o tempo limite da operação."
                : $"{provider.Name} falhou ao processar a operação.";

            attempts.Add(new ProviderAttemptResult(provider, outcome, message, failureReason));
        }

        return new ProviderExecutionResult(false, null, attempts);
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

    private static async Task UpdateCustomerBalanceAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid customerId,
        decimal availableBalance,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                update customer_accounts
                set available_balance = @AvailableBalance
                where id = @CustomerId;
                """,
                new
                {
                    AvailableBalance = availableBalance,
                    CustomerId = customerId
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task<decimal> GetOutstandingAmountAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string customerExternalId,
        CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<decimal>(
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
                    CustomerExternalId = customerExternalId,
                    SucceededStatus = OperationStatus.Succeeded
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task InsertOperationAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        OperationInsertRow operation,
        CancellationToken cancellationToken)
    {
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
                    available_balance_after,
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
                    @AvailableBalanceAfter,
                    @CreatedAt,
                    @ResponseMessage,
                    @FailureReason);
                """,
                operation,
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task InsertLedgerEntriesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<LedgerInsertRow> ledgerEntries,
        CancellationToken cancellationToken)
    {
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
                ledgerEntries,
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task InsertAuditEventsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<AuditEventRow> auditEvents,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                insert into operation_audit_events (
                    id,
                    operation_id,
                    customer_external_id,
                    idempotency_key,
                    event_type,
                    provider_code,
                    message,
                    created_at,
                    metadata)
                values (
                    @Id,
                    @OperationId,
                    @CustomerExternalId,
                    @IdempotencyKey,
                    @EventType,
                    @ProviderCode,
                    @Message,
                    @CreatedAt,
                    cast(@Metadata as jsonb));
                """,
                auditEvents,
                transaction,
                cancellationToken: cancellationToken));
    }

    private static AuditEventRow CreateAuditEvent(
        Guid operationId,
        string customerExternalId,
        string idempotencyKey,
        string eventType,
        string? providerCode,
        string message,
        DateTimeOffset createdAt,
        string? metadata) =>
        new(Guid.NewGuid(), operationId, customerExternalId, idempotencyKey, eventType, providerCode, message, createdAt, metadata);

    private static bool IsIdempotencyConstraintViolation(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(exception.ConstraintName, "billing_operations_idempotency_key_key", StringComparison.Ordinal);

    private static string ToJsonNumber(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string ToJsonBoolean(bool value) => value ? "true" : "false";

    private static InvoiceResponse MapInvoiceResponse(InvoiceRow row)
    {
        var (periodStart, periodEnd) = ResolveInvoicePeriod(row.ReferenceMonth);

        return new InvoiceResponse(
            row.InvoiceId,
            row.CustomerExternalId,
            row.CustomerName,
            row.AccountType,
            periodStart,
            periodEnd,
            ToDateTimeOffset(row.GeneratedAt),
            row.TotalAmount,
            row.OperationCount,
            "closed");
    }

    private static (DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd) ResolveInvoicePeriod(string referenceMonth)
    {
        var parts = referenceMonth.Split('-', StringSplitOptions.TrimEntries);
        var year = int.Parse(parts[0]);
        var month = int.Parse(parts[1]);
        var periodStart = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1).AddSeconds(-1);

        return (periodStart, periodEnd);
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : new DateTimeOffset(value);

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
        string Message,
        decimal AvailableBalance);

    private sealed record InvoiceRow(
        Guid InvoiceId,
        string CustomerExternalId,
        string CustomerName,
        AccountType AccountType,
        string ReferenceMonth,
        DateTime GeneratedAt,
        decimal TotalAmount,
        int OperationCount);

    private sealed record CustomerForUpdateRow(Guid Id, string ExternalId, AccountType AccountType, decimal AvailableBalance);

    private sealed record CustomerTypeRow(string ExternalId);

    private sealed record ProviderRow(string Code, string Name, int Priority);

    private sealed record OperationInsertRow(
        Guid Id,
        string IdempotencyKey,
        string CustomerExternalId,
        string Provider,
        string Description,
        OperationKind Kind,
        OperationStatus Status,
        decimal Amount,
        decimal AvailableBalanceAfter,
        DateTimeOffset CreatedAt,
        string ResponseMessage,
        string? FailureReason);

    private sealed record LedgerInsertRow(
        Guid Id,
        string CustomerExternalId,
        Guid OperationId,
        string Description,
        decimal Amount,
        decimal BalanceAfter,
        DateTimeOffset CreatedAt);

    private sealed record AuditEventRow(
        Guid Id,
        Guid? OperationId,
        string CustomerExternalId,
        string IdempotencyKey,
        string EventType,
        string? ProviderCode,
        string Message,
        DateTimeOffset CreatedAt,
        string? Metadata);

    private sealed record ProviderExecutionResult(
        bool Succeeded,
        ProviderRow? SuccessfulProvider,
        IReadOnlyList<ProviderAttemptResult> Attempts);

    private sealed record ProviderAttemptResult(
        ProviderRow Provider,
        ProviderAttemptOutcome Outcome,
        string Message,
        string? FailureReason);

    private enum ProviderAttemptOutcome
    {
        Succeeded,
        Failed,
        TimedOut
    }
}
