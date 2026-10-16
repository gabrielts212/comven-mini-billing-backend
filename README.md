# Comven Mini Billing Backend

Starter backend para o desafio da Comven com foco em separacao de responsabilidades e regras financeiras sensiveis.

## Estrutura

- src/Comven.Api: endpoints HTTP e composicao
- src/Comven.Application: contratos de casos de uso e DTOs
- src/Comven.Domain: entidades e regras basicas de saldo
- src/Comven.Infrastructure: adaptadores e servico seedado para a primeira entrega
- src/Comven.Worker: ciclo de fechamento de faturas
- tests/Comven.Tests: testes de dominio
- database/init.sql: schema inicial PostgreSQL com restricoes de integridade

## Estado do starter

Hoje a API roda com um servico seedado em memoria para permitir demonstracao imediata do fluxo no frontend.
O schema PostgreSQL e as dependencias de Dapper/Npgsql ja estao presentes para a proxima iteracao de persistencia real.

## Endpoints

- GET /api/health
- GET /api/statement?customerExternalId=cli-pre-001
- POST /api/operations
- POST /api/invoices/close

## Rodando localmente

```bash
dotnet build Comven.slnx
dotnet test Comven.slnx
dotnet run --project src/Comven.Api
```

## Payload de operacao

```json
{
  "customerExternalId": "cli-pre-001",
  "amount": 75,
  "description": "Pagamento instantaneo",
  "kind": 1,
  "idempotencyKey": "idem-123",
  "simulateProviderFailure": false
}
```

## Prioridade de proxima iteracao

1. Trocar o servico seedado por repositorios Dapper transacionais.
2. Persistir tentativas por fornecedor e estorno unico no banco.
3. Fechar faturas pos-pagas usando operacoes nao faturadas na mesma transacao.
