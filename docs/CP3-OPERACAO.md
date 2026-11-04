# CP3 — Mensageria, confirmação, idempotência e falhas

## Fila e mensagem

| Item | Configuração |
|---|---|
| Fila | `expensehub.reports`, exchange padrão (`""`), routing key = nome da fila |
| Durabilidade | `durable: true`, `exclusive: false`, `autoDelete: false` (declarada pela web e pelo worker) |
| Mensagem | `{"reportId":"<guid>"}`, `Persistent = true` (delivery mode 2), `MessageId`/`CorrelationId` = reportId, `mandatory: true` |
| Armazenamento do broker | volume `rabbitmq-data` em `/var/lib/rabbitmq`, `hostname` fixo `expensehub-rabbitmq` |
| Publicação | canal com *publisher confirms* e rastreamento (`RabbitMQ.Client` 7). O `await BasicPublishAsync` só termina depois do ack do broker; nack, mensagem devolvida (`mandatory`) ou timeout (`RabbitMq:PublishTimeoutSeconds`, padrão 10 s) geram falha |
| Consumo | `autoAck: false`, `prefetch = 1`, um worker |

O worker **não** confia no conteúdo da mensagem além do `reportId`. Período, escopo e solicitante são lidos do registro `ReportRequests` persistido pela web.

## Sequência normal

1. `POST /api/reports` valida as datas e o escopo e checa a role (`Mine` → Employee, `Global` → Auditor).
2. Grava `ReportRequests` com `Status = Pending` e `RequesterId` vindo do token.
3. Publica `{reportId}` e aguarda a confirmação do broker.
4. Com a confirmação: **202** + `Location: /api/reports/{id}`. Sem ela: **503** com `id` e `code = publish_unconfirmed`, e o registro continua `Pending`.
5. O worker recebe a mensagem e faz um `UPDATE` condicional `Pending|Processing → Processing`, que incrementa `Attempts` e preenche `StartedAt` (sem sobrescrever um valor anterior).
6. Calcula a partir dos reembolsos lidos **agora**, com `ExpenseDate` entre as datas (inclusive) e, em `Mine`, `OwnerId = RequesterId`.
7. Um único `UPDATE` condicional grava `Completed`, `ResultJson` e `CompletedAt`, somente se o status ainda não for `Completed`/`Failed`.
8. Só depois do passo 7 o worker envia o **ack**.

## Idempotência (persistente)

- A proteção está no banco, não na memória: os `UPDATE`s condicionais dos passos 5 e 7 não alteram registros já `Completed`/`Failed`. Um worker reiniciado lê o mesmo estado.
- Reentrega de uma mensagem já concluída: o worker lê `Completed`, registra `duplicate delivery ignored`, envia o ack e não recalcula nem altera `ResultJson`/`CompletedAt`.
- Um novo `POST` é outra solicitação, com outro id. Isso não é duplicata.
- Consultar (`GET /{id}`) e baixar o Excel leem `ResultJson`, nunca os reembolsos atuais. Alterar um reembolso depois da conclusão não muda o relatório.

## Falhas

| Situação | Comportamento |
|---|---|
| Banco indisponível **antes** de registrar | `503 database_unavailable`, sem `id` e sem publicação |
| Broker sem confirmação **depois** de registrar | `503 publish_unconfirmed` com `id`; registro `Pending`; sem republicação automática |
| Worker parado | A mensagem fica `Ready` na fila durável; o relatório continua `Pending`. Ao retomar, o worker consome o mesmo id |
| Worker cai durante o processamento (antes do ack) | A mensagem volta a `Ready` e é reentregue. O registro está em `Processing` e é **retomado** (o passo 5 aceita `Processing`) |
| Falha transitória (banco ocupado/indisponível, erro ao salvar) | Nada é confirmado: aguarda `TransientRetryDelaySeconds` (5 s) e faz `nack` com `requeue` |
| Limite de tentativas (`MaxAttempts` = 3) | Grava `Failed` com `error.code = max_attempts_exceeded` e envia o ack. Isso **encerra o reencaminhamento** |
| Registro persistido inválido (período invertido, escopo desconhecido) | Falha de negócio definitiva: `Failed` com `invalid_request` e ack |
| Mensagem ilegível ou id inexistente | Descartada com ack e log (não há registro para marcar) |
| Falha ao gravar `Failed` | Tratada como transitória: **não** há ack |

Mensagens de erro expostas são fixas e sanitizadas: nada de stack trace, SQL ou string de conexão. Os logs registram só o tipo da exceção.

### Worker parado × broker indisponível

São falhas diferentes:
- **Worker parado:** a publicação é confirmada e a API responde 202. O trabalho espera na fila (`Ready`) e é processado quando o worker volta.
- **Broker indisponível:** a publicação não é confirmada e a API responde 503 com o id persistido. Nenhuma mensagem existe na fila, então reiniciar o worker não resolve.

## Limite sem Transactional Outbox

Salvar no banco (passo 2) e publicar (passo 3) são operações separadas, sem transação entre elas:

- Se a web cair **entre** o passo 2 e o 3, o registro fica `Pending` e **nenhuma mensagem é publicada**. Nada vai processá-lo; reiniciar o worker **não** recupera uma mensagem que nunca existiu.
- Se o broker gravar a mensagem mas a confirmação se perder, a API responde 503 mesmo com a mensagem na fila. Nesse caso o worker pode concluir o relatório normalmente, e o cliente vê `Completed` ao consultar o id.

Mitigação adotada: o 503 sempre traz o `id` persistido, para o cliente consultar o estado. Se ficar `Pending`, o cliente faz um novo `POST`. Uma Transactional Outbox resolveria isso, mas não faz parte do escopo obrigatório e não foi implementada.

## Logs correlacionados (por `ReportId`)

| Evento | Processo | Mensagem |
|---|---|---|
| publicação confirmada | web | `Report {id} published (scope …)` |
| publicação não confirmada | web | `Report {id} persisted but publication was not confirmed by the broker` |
| recebimento | worker | `Report {id} received (redelivered: …)` |
| início | worker | `Report {id} processing started (attempt n)` |
| conclusão | worker | `Report {id} completed: n expenses` / `acknowledged (Completed)` |
| duplicata | worker | `Report {id} duplicate delivery ignored (status Completed)` |
| falha definitiva | worker | `Report {id} failed definitively (code)` |
| falha transitória | worker | `Report {id} transient failure (…)` / `requeued after transient failure` |

Tokens, senhas, cabeçalhos de autenticação e strings de conexão não são registrados.
