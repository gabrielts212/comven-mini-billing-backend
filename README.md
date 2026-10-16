## 1. Introducao 🚀

Esta aplicacao e um backend starter para o desafio da Comven, com foco em separacao de responsabilidades e em regras financeiras sensiveis.

O projeto foi organizado para isolar bem as camadas de API, aplicacao, dominio, infraestrutura e processamento assíncrono. A entrega atual prioriza a demonstracao do fluxo principal, mantendo a base preparada para a proxima iteracao com persistencia real.

## 2. Estrutura do projeto 🗂️

Os principais modulos do projeto sao:

- `src/Comven.Api`: endpoints HTTP e composicao da aplicacao.
- `src/Comven.Application`: contratos de casos de uso e DTOs.
- `src/Comven.Domain`: entidades e regras basicas de saldo.
- `src/Comven.Infrastructure`: adaptadores e servico seedado para a primeira entrega.
- `src/Comven.Worker`: ciclo de fechamento de faturas.
- `tests/Comven.Tests`: testes de dominio.
- `database/init.sql`: schema inicial PostgreSQL com restricoes de integridade.

## 3. Estado atual do projeto ⚙️

Hoje a API roda com um servico seedado em memoria para permitir demonstracao imediata do fluxo no frontend.

O schema PostgreSQL e as dependencias de Dapper/Npgsql ja estao presentes para a proxima iteracao de persistencia real.

## 4. Banco de dados e uso com Render 🗄️

O projeto possui uma base PostgreSQL modelada para suportar contas, operacoes financeiras, lancamentos de extrato, faturas e eventos de auditoria.

O arquivo `database/init.sql` cria tabelas, views, indices e dados iniciais para demonstracao, enquanto a infraestrutura usa `Dapper` com `Npgsql` para conexao e acesso ao banco.

Localmente, o banco pode ser executado com Docker, e o `docker-compose.yml` ja conecta a API ao servico `postgres`, alem de montar o arquivo de inicializacao automaticamente.

No deploy com Render, a ideia e usar um servico PostgreSQL gerenciado e informar a string de conexao pela variavel `ConnectionStrings__Postgres`. Assim, a aplicacao continua usando a mesma estrutura de banco, mudando apenas a configuracao do ambiente.

A API e o Worker tambem executam a inicializacao do schema na subida da aplicacao, o que ajuda a manter o banco pronto para uso tanto localmente quanto em hospedagem.

## 5. Endpoints disponiveis 🌐

Estes sao os endpoints atualmente expostos pela API:

- `GET /api/health`
- `GET /api/statement?customerExternalId=cli-pre-001`
- `POST /api/operations`
- `POST /api/invoices/close`

## 6. Como rodar localmente ▶️

Primeiro passo: compile o projeto.

```bash
dotnet build Comven.slnx
```

Depois: execute os testes.

```bash
dotnet test Comven.slnx
```

Por fim: suba a API localmente. ✔️

```bash
dotnet run --project src/Comven.Api
```

## 7. Exemplo de payload de operacao 🧾

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

## 8. Prioridades da proxima iteracao 🔄

1. Trocar o servico seedado por repositorios Dapper transacionais.
2. Persistir tentativas por fornecedor e estorno unico no banco.
3. Fechar faturas pos-pagas usando operacoes nao faturadas na mesma transacao.
