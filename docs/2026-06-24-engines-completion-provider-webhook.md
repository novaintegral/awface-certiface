> Atualização: o provedor Certiface não dispara webhook terminal para o AWFace neste fluxo. O comportamento vigente é: o AWFace processa o retorno do SDK FaceTec, consulta `/document/result` imediatamente quando `codID = 200`, persiste o resultado/face e dispara o webhook do Tenant. Para `codID = 300.1`, persiste o retorno do SDK, notifica o Tenant e mantém retentativa. Para `codID = 300.2`, persiste o retorno do SDK, notifica o Tenant e finaliza a jornada.
# Registro técnico AWFace - 24 de junho de 2026

Este documento registra as alterações realizadas no fluxo de prova de vida do
AWFace em 24 de junho de 2026.

## 1. Engines FaceTec V9 e V10

O AWFace passou a suportar dois engines FaceTec:

- `V9`, usando os assets em `/assets/9.7.109`;
- `V10`, usando os assets em `/assets/10.0.42`.

O engine padrão para novas jornadas é configurado no backend:

```json
{
  "Awface": {
    "LivenessEngine": "V10"
  }
}
```

No Docker Compose:

```env
AWFACE_LIVENESS_ENGINE=V10
```

Valores aceitos: `V9` e `V10`. O engine é persistido na jornada, portanto uma
alteração de ambiente afeta somente novas jornadas.

### Endpoints internos por engine

FaceTec V9:

```http
POST /api/awface/facetec/v9/3d/initialize
POST /api/awface/facetec/v9/3d/session-token
POST /api/awface/facetec/v9/3d/liveness
```

FaceTec V10:

```http
POST /api/awface/facetec/v10/3d/process-request
POST /api/awface/facetec/3d/process-request
```

O segundo endpoint V10 é mantido como alias legado. O frontend escolhe o SDK
por meio do engine salvo na jornada e chama somente a AWFace.Api.

### Retentativas e appkey na V9

A V9 consome a appkey durante o liveness. Uma retentativa V9 solicita uma nova
appkey, e cada emissão é persistida em
`awface_liveness_appkey_history`, contendo jornada, engine, tentativa, data de
emissão e data de substituição.

A appkey atual também permanece na jornada. O histórico permite investigar
tentativas anteriores e localizar a jornada quando a Certiface notificar uma
appkey já substituída.

## 2. Novo comportamento de `/completion`

Após o SDK enviar a prova de vida com sucesso, o usuário é redirecionado
imediatamente para:

```text
/#/journey-completion
```

O redirecionamento não aguarda o processamento final da Certiface nem a entrega
do webhook do Tenant.

A tela consulta:

```http
GET /api/awface/journeys/{journeyId}/completion
```

A primeira consulta acontece ao abrir a tela. Enquanto o resultado estiver
pendente, novas consultas são feitas a cada **5 segundos**.

### Estados

`PENDING`

- mantém o spinner e a barra de progresso indeterminada;
- informa que o processamento está em andamento;
- erros HTTP transitórios não encerram a consulta.

`SUCCESS`

- o processamento do provedor foi aceito;
- o webhook do Tenant retornou HTTP `2xx`;
- inicia a contagem regressiva e tenta fechar a aba.

`FAILED`

- o provedor informou erro terminal ou a entrega ao webhook do Tenant falhou;
- apresenta a mensagem de falha na tela.

## 3. Processamento após retorno do SDK FaceTec

O endpoint de webhook terminal do provedor foi removido do fluxo ativo. A decisão da jornada nasce do retorno do SDK FaceTec recebido pelos endpoints internos V9/V10. Quando `codID = 200`, o AWFace consulta `/facecaptcha/service/captcha/document/result` imediatamente para enriquecer a resposta antes de disparar o Tenant. Quando `codID = 300.1`, usa o retorno do SDK e mantém a jornada ativa para retentativa. Quando `codID = 300.2`, usa o retorno do SDK, notifica o Tenant e finaliza a jornada.

## 4. Webhook enviado ao Tenant

Depois de processar a notificação terminal, o AWFace envia o resultado ao
`UrlCallback` do Tenant.

O callback pode usar OAuth2 Client Credentials. Quando habilitado, o AWFace
obtém e renova o `access_token`, respeitando `expires_in`, e envia:

```http
Authorization: Bearer <access_token>
```

O payload não contém `responseBlob` e inclui a localização, quando autorizada:

```json
{
  "status": "Completo",
  "appkey": "provider-appkey",
  "journeyId": "00000000-0000-0000-0000-000000000000",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "idExternoCliente": "123456",
  "deviceLocation": {
    "latitude": -23.55052,
    "longitude": -46.633308,
    "accuracy": 18.4,
    "capturedAt": "2026-06-24T21:04:30.000Z"
  },
  "result": {
    "valid": true,
    "codID": 200,
    "hash": "resultado",
    "protocol": "202600287025",
    "retry": false
  }
}
```

O Tenant deve responder HTTP `2xx`. Essa resposta é necessária para que
`/completion` retorne `SUCCESS`.

## 5. Exceção temporária de homologação

Durante a homologação, a Certiface pode notificar `Completo`, mas
`/document/result` ainda responder `Não processado`.

O comportamento temporário é controlado por:

```json
{
  "Awface": {
    "Homologation": {
      "AcceptNonProcessedResultAfterCompleteNotification": true
    }
  }
}
```

No Docker Compose:

```env
AWFACE_HOMOLOGATION_ACCEPT_NON_PROCESSED_RESULT=true
```

A exceção somente se aplica quando a configuração está habilitada, o webhook
informou `Completo` e `/document/result` retornou `Não processado`. O backend
registra um warning. Em produção, mantenha a configuração como `false`.

## 6. Banco de dados e migrations

Migrations relacionadas:

```text
docker/postgres/migrations/202606240001_liveness_engine.sql
docker/postgres/migrations/202606240002_liveness_appkey_history.sql
docker/postgres/migrations/202606240003_provider_completion_webhook.sql
```

Principais estruturas:

- engine em `awface_liveness_journey`;
- histórico em `awface_liveness_appkey_history`;
- submissão pendente vinculada à appkey;
- notificação idempotente em `awface_provider_notification`;
- resultado final e entrega ao webhook do Tenant.

## 7. Checklist de homologação

1. Reiniciar a AWFace.Api após alterar configurações.
2. Criar uma jornada V9 e outra V10.
3. Confirmar o redirecionamento imediato para `/journey-completion`.
4. Confirmar consultas a `/completion` em intervalos de 5 segundos.
5. Enviar uma notificação `Completo` ao webhook da Certiface.
6. Confirmar a consulta posterior a `/document/result`.
7. Confirmar persistência do resultado e da imagem.
8. Confirmar somente uma entrega ao webhook do Tenant.
9. Confirmar que `/completion` muda de `PENDING` para `SUCCESS`.
10. Reenviar a notificação e confirmar a idempotência.

