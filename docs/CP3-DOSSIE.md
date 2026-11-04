# CP3 — Dossiê de experimentos (E01–E05)

> Preencha cada `PREENCHER` com a saída **real** do ambiente (copie do terminal ou do `.http`).
> **Não cole tokens, senhas nem o cabeçalho `Authorization`.** Ids de relatório podem aparecer.

## Ambiente

| Item | Valor |
|---|---|
| Commit final | `PREENCHER_SHA` |
| Imagem web | `ghcr.io/PREENCHER/expensehub-web:PREENCHER_SHA` |
| Imagem worker | `ghcr.io/PREENCHER/expensehub-worker:PREENCHER_SHA` |
| Versão em execução | saída de `curl http://localhost:8080/health` → `PREENCHER` |

Preparação (uma vez): `cp .env.example .env`, preencher, `docker compose pull && docker compose up -d`. Depois rode as seções 1–4 do `.http` (usuários, roles, logins e dados conhecidos).

Para conferir a fila durante os experimentos:

```bash
docker compose exec rabbitmq rabbitmqctl list_queues name durable messages_ready messages_unacknowledged
```

---

## E01 — Fluxo normal com Excel

1. Seção 5 do `.http`: Alice faz `POST /api/reports` com `Mine` e setembro de 2026.
2. Guarde o `id` e o `Location` do 202.
3. Faça `GET /api/reports/{id}` até `Completed`.
4. Faça `GET /api/reports/{id}/excel` e salve como `report-{id}.xlsx`.

| Evidência | Resultado |
|---|---|
| Status e `Location` do POST | `PREENCHER (202, /api/reports/…)` |
| `id` | `PREENCHER` |
| Envelope final | `PREENCHER` (esperado `totalCount: 3`, `totalAmount: 175.00`, Approved 2/150.00, Paid 1/25.00, demais 0) |
| Planilha | abriu no Excel? `PREENCHER`; total 3 / 175,00 igual ao JSON? `PREENCHER` |
| Logs | `docker compose logs web worker \| grep <id>` → `PREENCHER` |

## E02 — Worker parado

```bash
docker compose stop worker
# POST /api/reports (Alice, Mine) → guarde o id
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready messages_unacknowledged
# GET /api/reports/{id} → Pending
docker compose up -d worker
# GET /api/reports/{id} → Completed (mesmo id)
docker compose logs --tail 80 worker
```

| Evidência | Resultado |
|---|---|
| id | `PREENCHER` |
| Fila com o worker parado | `expensehub.reports  Ready=1  Unacked=0` → `PREENCHER` |
| Status com o worker parado | `Pending` → `PREENCHER` |
| Status após retomar (mesmo id) | `Completed` → `PREENCHER` |
| Logs do worker com o id | `received` / `processing started` / `completed` → `PREENCHER` |

Interpretação: `Ready` é uma métrica do broker (mensagem disponível) e `Pending` é o estado da aplicação. A correlação entre os dois é feita pelo `reportId` nos logs.

## E03 — Duplicata após reinício

1. Use um id já `Completed` (o do E01). Anote `result.generatedAt` e `completedAt`.
2. Rode `docker compose restart worker`.
3. Republique **a mesma mensagem** (sem `POST` novo):

```bash
source .env
curl -s -u "$RABBITMQ_USER:$RABBITMQ_PASSWORD" -H "content-type: application/json" \
  -X POST http://localhost:15672/api/exchanges/%2F/amq.default/publish \
  -d '{"properties":{"delivery_mode":2},"routing_key":"expensehub.reports","payload":"{\"reportId\":\"COLE_O_ID\"}","payload_encoding":"string"}'
# resposta esperada: {"routed":true}
```

Também é possível pelo painel (`http://localhost:15672` → Queues → `expensehub.reports` → *Publish message*, Delivery mode 2, payload `{"reportId":"<id>"}`).

4. Faça `GET /api/reports/{id}` e compare com o passo 1.

| Evidência | Resultado |
|---|---|
| id | `PREENCHER` |
| `generatedAt`/`completedAt` antes | `PREENCHER` |
| `generatedAt`/`completedAt` depois | `PREENCHER` (iguais) |
| Log do worker | `Report <id> duplicate delivery ignored (status Completed)` e `acknowledged (Duplicate)` → `PREENCHER` |
| Fila depois | `Ready=0 Unacked=0` → `PREENCHER` |

A proteção sobrevive ao reinício porque está no banco (UPDATE condicional), não em memória.

## E04 — Isolamento

Seção 6 do `.http`:

| Cenário | Esperado | Obtido |
|---|---|---|
| Sem token `GET /api/reports` | 401 | `PREENCHER` |
| Bob (Employee) `POST` Global | 403 | `PREENCHER` |
| Bob `GET /api/reports/{id da Alice}` | 404 | `PREENCHER` |
| Bob `GET /api/reports/{id da Alice}/excel` | 404 | `PREENCHER` |
| Auditor `POST` Global | 202 → Completed com 4 / 375.00 | `PREENCHER` |
| Auditor `GET` relatório da Alice | 404 | `PREENCHER` |
| Auditor `GET` Excel da Alice | 404 | `PREENCHER` |
| Auditor (sem Employee) `POST` Mine | 403 | `PREENCHER` |
| Admin `POST` Global | 403 | `PREENCHER` |
| Bob `GET /api/reports` | só os relatórios do Bob | `PREENCHER` |

## E05 — Persistência e versão

```bash
docker compose up -d --force-recreate web worker   # não remove volumes nem o banco
docker compose ps
docker compose images web worker
curl http://localhost:8080/health
# GET /api/reports/{id do E01} (o token anterior continua válido; se precisar, faça login de novo)
```

| Evidência | Resultado |
|---|---|
| Imagens/tags em execução | `PREENCHER` (SHA do commit, não `latest`) |
| `/health` → `version` | `PREENCHER` |
| Relatório do E01 após recriar | `Completed`, mesmo `generatedAt` e mesmos totais → `PREENCHER` |

## Diagnóstico

- **Aceitação × conclusão:** 202 significa "registrado e confirmado pelo broker", não "calculado". A conclusão é o estado `Completed`, gravado pelo worker junto com o resultado.
- **Worker parado × broker indisponível:** no primeiro caso, a mensagem espera na fila durável e o relatório é concluído quando o worker volta (E02). No segundo, não há confirmação: a API responde 503 com o id persistido e nada entra na fila (coberto no código e no teste `Create_PublishNotConfirmed_ReturnsUnavailableWithPersistedId`).
- **Duplicata × novo POST:** reentregar o mesmo `reportId` não produz novo efeito (E03). Um novo POST cria outro id e outro cálculo.
- **Janela sem outbox:** uma queda entre salvar e publicar deixa o registro `Pending` sem mensagem, e reiniciar o worker não o recupera. Veja [CP3-OPERACAO.md](CP3-OPERACAO.md#limite-sem-transactional-outbox).
