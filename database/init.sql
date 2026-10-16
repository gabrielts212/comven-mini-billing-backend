create table if not exists customer_accounts (
    id uuid primary key,
    external_id varchar(64) not null unique,
    name varchar(200) not null,
    account_type integer not null check (account_type in (1, 2)),
    available_balance numeric(18, 2) not null,
    billing_day integer not null check (billing_day between 1 and 28),
    created_at timestamptz not null default now()
);

create table if not exists billing_operations (
    id uuid primary key,
    idempotency_key varchar(120) not null unique,
    customer_external_id varchar(64) not null references customer_accounts(external_id),
    provider varchar(80) not null,
    description varchar(300) not null,
    kind integer not null check (kind in (1, 2, 3)),
    status integer not null check (status in (1, 2, 3, 4)),
    amount numeric(18, 2) not null,
    created_at timestamptz not null,
    response_message varchar(300) not null,
    failure_reason varchar(300) null,
    invoice_id uuid null
);

create table if not exists ledger_entries (
    id uuid primary key,
    customer_external_id varchar(64) not null references customer_accounts(external_id),
    operation_id uuid not null references billing_operations(id),
    description varchar(300) not null,
    amount numeric(18, 2) not null,
    balance_after numeric(18, 2) not null,
    created_at timestamptz not null
);

create table if not exists invoices (
    id uuid primary key,
    customer_external_id varchar(64) not null references customer_accounts(external_id),
    reference_month char(7) not null,
    amount numeric(18, 2) not null,
    closed_at timestamptz not null,
    operation_count integer not null,
    constraint uq_invoices_customer_month unique (customer_external_id, reference_month)
);

create index if not exists ix_billing_operations_customer_status_invoice
    on billing_operations (customer_external_id, status, invoice_id);

create index if not exists ix_ledger_entries_customer_created_at
    on ledger_entries (customer_external_id, created_at desc);

create or replace view billing_statement_items as
select
    le.customer_external_id,
    le.operation_id,
    le.description,
    le.amount,
    le.balance_after,
    le.created_at,
    timezone('utc', le.created_at)::date as created_on,
    bo.provider,
    bo.kind,
    bo.status
from ledger_entries le
join billing_operations bo on bo.id = le.operation_id;

create or replace view billing_operation_replays as
select
    bo.idempotency_key,
    bo.id as operation_id,
    bo.status,
    bo.provider,
    bo.response_message as message,
    ca.available_balance
from billing_operations bo
join customer_accounts ca on ca.external_id = bo.customer_external_id;

insert into customer_accounts (id, external_id, name, account_type, available_balance, billing_day)
values
    ('11111111-1111-1111-1111-111111111111', 'cli-pre-001', 'Ada Lovelace', 1, 1500.00, 5),
    ('22222222-2222-2222-2222-222222222222', 'cli-post-001', 'Grace Hopper', 2, 0.00, 15)
on conflict (external_id) do nothing;

insert into billing_operations (id, idempotency_key, customer_external_id, provider, description, kind, status, amount, created_at, response_message, failure_reason, invoice_id)
values
    ('33333333-3333-3333-3333-333333333331', 'seed-pre-1', 'cli-pre-001', 'Provider A', 'Recarga inicial', 1, 2, 500.00, now() - interval '4 day', 'Operação registrada com sucesso.', null, null),
    ('33333333-3333-3333-3333-333333333332', 'seed-pre-2', 'cli-pre-001', 'Provider B', 'Pagamento de boleto', 1, 2, 120.00, now() - interval '2 day', 'Operação registrada com sucesso.', null, null),
    ('33333333-3333-3333-3333-333333333333', 'seed-post-1', 'cli-post-001', 'Provider C', 'Compra pós-paga', 1, 2, 320.00, now() - interval '3 day', 'Operação registrada com sucesso.', null, null)
on conflict (idempotency_key) do nothing;

insert into ledger_entries (id, customer_external_id, operation_id, description, amount, balance_after, created_at)
values
    ('44444444-4444-4444-4444-444444444441', 'cli-pre-001', '33333333-3333-3333-3333-333333333331', 'Recarga inicial', 500.00, 1620.00, now() - interval '4 day'),
    ('44444444-4444-4444-4444-444444444442', 'cli-pre-001', '33333333-3333-3333-3333-333333333332', 'Pagamento de boleto', -120.00, 1500.00, now() - interval '2 day'),
    ('44444444-4444-4444-4444-444444444443', 'cli-post-001', '33333333-3333-3333-3333-333333333333', 'Compra pós-paga', 320.00, 320.00, now() - interval '3 day')
on conflict (id) do nothing;
