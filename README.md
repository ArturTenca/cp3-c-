# ExpenseHub — Checkpoint 3 (relatórios em segundo plano)

API corporativa de reembolsos (CP2) evoluída com **relatórios assíncronos**: a aplicação web registra o pedido e publica no **RabbitMQ**; um **worker C#** consome, calcula e persiste; o usuário acompanha pelo id e baixa o resultado em **Excel (.xlsx)**.

## Integrantes

| Nome | RM |
|---|---|
| Victor Mattenhauer Lopes Capp | 555753 |
| Artur Alves Tenca | 555171 |
| Igor Brunelli Ralo | 555035 |

Especificação: issues `CP3-I01` a `CP3-I07` de [Racass/checkpoint-csharpracass-expensehub](https://github.com/Racass/checkpoint-csharpracass-expensehub/issues?q=is%3Aissue+CP3).

| Referência | Commit |
|---|---|
| Base do CP2 | `PREENCHER` (o grupo não entregou o CP2; a base foi construída neste repositório a partir do template) |
| Entrega final do CP3 | `PREENCHER_SHA_FINAL` |

## Arquitetura

```text
Usuário ──HTTP──▶ ExpenseHub.Api ──publica {reportId} (confirm)──▶ RabbitMQ ──▶ ExpenseHub.Worker
                      │  registra Pending                    fila durável           │ ack só depois de salvar
                      ▼                                                             ▼
                 SQLite compartilhado (volume app-data)  ◀── lê período/escopo/solicitante, grava resultado
```

```text
sources/
├── ExpenseHub.Core/        domínio, EF Core (SQLite), regras de reembolso e de relatório, Excel
├── ExpenseHub.Api/         endpoints (Identity bearer, CP2 e CP3), publisher RabbitMQ
├── ExpenseHub.Worker/      consumidor RabbitMQ (BackgroundService)
└── ExpenseHub.UnitTests/   MSTest, sem banco/rede/RabbitMQ reais
docker/                     Dockerfiles da web e do worker
compose.yaml                RabbitMQ + imagens publicadas + volumes persistentes
```

- **Banco:** SQLite em arquivo no volume `app-data`, compartilhado pelos dois processos (modo WAL e `Default Timeout=30`). O schema é criado por `EnsureCreated` na subida da web (procedimento documentado; sem migrations). A web também cria as roles e a única conta Admin a partir de variáveis de ambiente.
- **Autenticação:** ASP.NET Core Identity com bearer token (`POST /register`, `POST /login`). As chaves de Data Protection ficam em `/data/keys`, então os tokens continuam válidos após recriar os contêineres. Alterar as roles de um usuário invalida os tokens dele.

## Endpoints do CP3

| Rota | Resposta |
|---|---|
| `POST /api/reports` `{fromDate,toDate,scope}` | `202` + `Location` + envelope (após persistir **e** receber confirmação do broker) · `400` datas/escopo inválidos · `403` perfil sem o escopo · `503` com `id` se a publicação não foi confirmada |
| `GET /api/reports` | `200`, somente solicitações do próprio usuário |
| `GET /api/reports/{id}` | `200` envelope · `404` inexistente/alheia (inclusive para Auditor) |
| `GET /api/reports/{id}/excel` | `200` `.xlsx` (`report-{id}.xlsx`) · `409` própria não concluída/Failed · `404` alheia |

Matriz: `Mine` exige Employee; `Global` exige Auditor; Admin/Approver/Finance sem esses perfis recebem `403`. O solicitante vem sempre do token; a mensagem carrega só `reportId`.

Os detalhes de fila, confirmações, idempotência e falhas estão em [docs/CP3-OPERACAO.md](docs/CP3-OPERACAO.md).

## Executar com as imagens publicadas

Pré-requisito: Docker com Compose.

```bash
cp .env.example .env          # preencha WEB_IMAGE/WORKER_IMAGE (tag = SHA do commit) e as senhas
docker compose pull
docker compose up -d
docker compose ps
curl http://localhost:8080/health          # {"status":"ok","version":"<sha>"}
docker compose logs --tail 80 worker
```

- API: `http://localhost:8080` · OpenAPI: `http://localhost:8080/openapi/v1.json`
- Painel do RabbitMQ: `http://localhost:15672` (usuário/senha do `.env`)
- Chamadas prontas: [sources/ExpenseHub.Api/ExpenseHub.Api.http](sources/ExpenseHub.Api/ExpenseHub.Api.http). Ele cria os usuários e os dados da tabela da apostila e cobre o fluxo completo e os casos negativos.

### Imagens

O workflow [`images`](.github/workflows/images.yml) roda os testes unitários e, **somente se passarem**, publica no GHCR as duas imagens com a tag igual ao SHA completo do commit:

```text
ghcr.io/<owner>/expensehub-web:<sha>
ghcr.io/<owner>/expensehub-worker:<sha>
```

Para a correção conseguir baixar as imagens, os pacotes precisam estar públicos (GitHub → Packages → pacote → Package settings → Change visibility → Public). A imagem também traz o commit no label `org.opencontainers.image.revision` e na variável `APP_VERSION`, exibida em `/health` e no log de inicialização do worker.

## Executar localmente (sem Docker)

```bash
docker run -d --name rabbit -p 5672:5672 -p 15672:15672 rabbitmq:4-management
cd sources
dotnet user-secrets init --project ExpenseHub.Api
dotnet user-secrets set "Seed:AdminEmail" "admin@expensehub.local" --project ExpenseHub.Api
dotnet user-secrets set "Seed:AdminPassword" "<senha>" --project ExpenseHub.Api
dotnet user-secrets set "RabbitMq:Password" "guest" --project ExpenseHub.Api
dotnet run --project ExpenseHub.Api --urls http://localhost:8080
# outro terminal (o worker usa ../ExpenseHub.Api/expensehub.db por padrão)
RabbitMq__Password=guest dotnet run --project ExpenseHub.Worker
```

## Testes

```bash
dotnet test sources/ExpenseHub.slnx
```

Os testes unitários não usam banco, rede nem RabbitMQ reais: usam fakes de repositório, publisher e store do worker. Cobrem o cálculo com os dados da apostila (Mine 3/R$ 175,00 e Global 4/R$ 375,00), os limites inclusivos do período, o conjunto vazio, a validação de período, as negações por perfil/escopo, o isolamento por solicitante, a publicação sem confirmação (503), a duplicata sem novo efeito, a consulta sem recálculo após alterar reembolsos, a retomada de `Processing`, a falha definitiva, e o Excel (planilha válida, células numéricas, 409/404).

As evidências operacionais (E01–E05) estão em [docs/CP3-DOSSIE.md](docs/CP3-DOSSIE.md).
