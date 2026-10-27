# AWFace.Api - Endpoints públicos para aplicações Host

Esta referência descreve a superfície pública que uma aplicação Host deve usar para integrar com o AWFace.

Base local padrão:

```text
http://localhost:5000
```

Em deploy on-premise com Nginx, a API também fica disponível pelo mesmo domínio do frontend no prefixo `/api`.

## Autenticação da aplicação Host

O Host deve usar o `Token de integração` do Tenant.

Para criar uma jornada autônoma, o token é enviado no corpo da requisição como `integrationToken`.

Para consultar resultado e imagem da face, envie o token preferencialmente no header:

```http
X-AWFace-Integration-Token: awf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Também é aceito via query string (desaconselhado: o token fica registrado em logs de proxies e no histórico):

```text
?integrationToken=awf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

## 1. Criar lançamento de jornada

Cria uma jornada autônoma e retorna uma URL para redirecionar o usuário.

```http
POST /api/awface/journey-launches
Content-Type: application/json
```

Request:

```json
{
  "integrationToken": "awf_demo_token",
  "journeyType": "LIVENESS_FACE_BUREAU",
  "cpf": "27100816874",
  "fullName": "Marcelo Nunes Ferreira",
  "birthDate": "1979-05-28",
  "externalClientId": "123456",
  "hostReference": "document-789",
  "metadata": {
    "documentId": "document-789",
    "contractId": "contract-456"
  }
}
```

Campos:

- `integrationToken`: token de integração do Tenant.
- `journeyType`: `LIVENESS`, `LIVENESS_FACE_BUREAU` ou `LIVENESS_FACE_BUREAU_DOCUMENT`.
- `cpf`: CPF do usuário, com ou sem máscara.
- `fullName`: nome completo do usuário.
- `birthDate`: data de nascimento em `yyyy-MM-dd`.
- `externalClientId`: identificador do usuário/processo no Host.
- `hostReference`: referência opcional do Host.
- `metadata`: objeto opcional com dados auxiliares do Host.

Response `200`:

```json
{
  "journeyId": "00000000-0000-0000-0000-000000000000",
  "launchToken": "opaque-short-lived-token",
  "launchUrl": "http://localhost:4200/#/journey-launch?token=opaque-short-lived-token",
  "expiresAt": "2026-06-17T21:00:00Z"
}
```

Após receber a resposta, o Host deve redirecionar o usuário para `launchUrl`.

Observações:

- O `launchToken` pode ser reutilizado enquanto não expirar.
- O AWFace rejeita token inválido ou expirado.
- O controle mais importante da jornada é a expiração da `appkey` do provedor de liveness.

Erros comuns:

- `400`: dados inválidos, Tenant não encontrado, Tenant bloqueado/cancelado ou tipo de jornada não habilitado.

## 2. Consultar resultado da prova de vida

Encapsula a consulta ao endpoint Certiface `/facecaptcha/service/captcha/document/result`.
Internamente, o AWFace chama a Certiface com `Content-Type: application/x-www-form-urlencoded` e body param `appkey`.

```http
GET /api/awface/journeys/{journeyId}/result
X-AWFace-Integration-Token: awf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Response `200`:

```json
{
  "status": "Completo",
  "valid": true
}
```

O corpo retornado é o JSON bruto retornado pela Certiface para a consulta de resultado.

Erros comuns:

- `401`: token de integração ausente ou inválido.
- `403`: jornada pertence a outro Tenant.
- `404`: jornada não encontrada.
- `400`: jornada ainda não possui `appkey`.
- `502`: falha ao consultar o provedor Certiface.

## 3. Consultar imagem da face da prova de vida

Retorna a imagem da face frontal salva pelo AWFace em base64 puro. A origem da imagem é o atributo `fotos.facecaptcha.frontal` retornado pela Certiface no `/document/result`.

O arquivo fica criptografado no storage persistente do AWFace com AES-256-GCM. O banco guarda apenas metadados, como `storage_key`, `sha256`, `content_type`, `encryption_algorithm` e tamanho.

```http
GET /api/awface/journeys/{journeyId}/face-image
X-AWFace-Integration-Token: awf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Response `200`:

```json
{
  "journeyId": "00000000-0000-0000-0000-000000000000",
  "assetType": "FRONTAL",
  "contentType": "image/jpeg",
  "imageBase64": "/9j/4AAQSkZJRgABAQ...",
  "encoding": "base64",
  "sha256": "e3b0c44298fc1c149afbf4c8996fb924...",
  "sizeBytes": 123456,
  "encryptionAlgorithm": "AES-256-GCM",
  "createdAt": "2026-06-17T21:05:00-03:00"
}
```

Use apenas o valor do campo `imageBase64` para converter a imagem. Não concatene o JSON completo nem prefixos como `data:image/jpeg;base64,`.

Se a aplicação Host preferir evitar tratamento de base64, use o endpoint binário:

```http
GET /api/awface/journeys/{journeyId}/face-image/file
X-AWFace-Integration-Token: awf_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

Response `200`:

```http
Content-Type: image/jpeg
Content-Disposition: attachment; filename="{journeyId}-frontal.jpg"
```

Erros comuns:

- `401`: token de integração ausente ou inválido.
- `403`: jornada pertence a outro Tenant.
- `404`: jornada não encontrada ou imagem ainda não disponível.

## 4. UrlCallback do Tenant

Quando a prova de vida termina (`codID` 200, 300.1 ou 300.2), o AWFace envia um `POST` para o `UrlCallback` configurado no Tenant.

A entrega é feita por uma fila persistente (outbox), desacoplada da tela do usuário:

- cada evento é entregue **uma única vez** por submissão do SDK (repetições do navegador não geram novo webhook);
- se o Host não responder HTTP `2xx`, o AWFace tenta novamente com intervalos crescentes (5 s, 15 s, 30 s, 1 min, 5 min, 15 min, 1 h, 3 h, 6 h), até `AWFACE_WEBHOOK_MAX_ATTEMPTS` tentativas (padrão 10);
- cada tentativa fica registrada em `awface_callback_delivery`.

Como uma entrega pode se repetir após falha de rede do lado do Host, **use `X-AWFace-Event-Id` (ou o campo `eventId`) para descartar duplicidades**.

Headers enviados:

```http
Content-Type: application/json; charset=utf-8
X-AWFace-Event-Id: 6f1c2f3e-6f0b-4b7e-9b39-2a1c7f5b9a10
X-AWFace-Timestamp: 1798400000
X-AWFace-Signature: t=1798400000,v1=<hex(HMAC-SHA256(secureCallbackToken, "1798400000." + corpo))>
X-AWFace-SecureCallback: <secureCallbackToken-do-tenant>
```

### Validação da assinatura (recomendada)

1. Leia o corpo **bruto** da requisição, antes de qualquer desserialização.
2. Extraia `t` e `v1` do header `X-AWFace-Signature`.
3. Rejeite se `|agora - t|` for maior que 5 minutos (proteção contra replay).
4. Calcule `HMAC-SHA256(chave = secureCallbackToken, mensagem = t + "." + corpo)` em hexadecimal minúsculo.
5. Compare com `v1` usando comparação de tempo constante.

Exemplo em C#:

```csharp
var expected = Convert.ToHexString(
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secureCallbackToken), Encoding.UTF8.GetBytes($"{t}.{rawBody}"))
).ToLowerInvariant();
var valid = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1));
```

O header `X-AWFace-SecureCallback` continua sendo enviado por compatibilidade, mas a validação por assinatura é a recomendada: o segredo trafega em claro no header legado.

Quando a autenticação OAuth2 estiver habilitada no Tenant, o AWFace obtém um `access_token` via client credentials antes de enviar o callback e adiciona:

```http
Authorization: Bearer <access_token>
```

A chamada de token é feita com `Content-Type: application/x-www-form-urlencoded` e body `grant_type=client_credentials`, `client_id` e `client_secret`. Quando a resposta contém `expires_in`, o token é mantido em cache e renovado automaticamente após expiração.

Payload:

```json
{
  "eventId": "6f1c2f3e-6f0b-4b7e-9b39-2a1c7f5b9a10",
  "status": "Completo",
  "appkey": "provider-appkey",
  "journeyId": "00000000-0000-0000-0000-000000000000",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "idExternoCliente": "123456",
  "deviceLocation": {
    "latitude": -23.55052,
    "longitude": -46.633308,
    "accuracy": 18.4,
    "capturedAt": "2026-06-17T21:04:30.000Z"
  },
  "result": {
    "valid": true,
    "codID": 200,
    "hash": "resultado",
    "protocol": "202600287025",
    "retry": false,
    "launchId": "0d973a04-df4f-42a5-ab24-b0ac07d0e375"
  }
}
```

Observações:

- O atributo `responseBlob` é removido do payload enviado ao Host.
- `deviceLocation` pode ser `null` quando o usuário negar a permissão de localização, o browser não suportar geolocalização ou a captura exceder o timeout.
- O Host deve responder HTTP `2xx` para que o AWFace considere o callback entregue com sucesso.
- Responda rapidamente (timeout padrão de 10 s) e processe de forma assíncrona no Host, se necessário.

## Erros

Erros de autenticação e falhas inesperadas seguem o formato
[ProblemDetails (RFC 7807)](https://www.rfc-editor.org/rfc/rfc7807), com `traceId` para suporte:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Acesso negado",
  "status": 403,
  "detail": "A jornada pertence a outro Tenant.",
  "traceId": "0HN7..."
}
```

Erros de validação de campos (`400`) mantêm a lista `[{ "field": "...", "message": "..." }]`.

## Endpoints internos do fluxo AWFace

Os endpoints abaixo existem, mas não devem ser chamados diretamente pela aplicação Host em produção.
A partir da 1.1.0 eles exigem o header `X-AWFace-Journey-Token`, emitido somente para o navegador do
usuário no consumo do `launchToken` (ou na criação da jornada assistida):

- `POST /api/awface/journey-launches/{launchToken}/consume` (emite o token; limitado a 30 req/min por IP)
- `POST /api/awface/journeys` (jornada assistida; emite o token; limitado a 30 req/min por IP)
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `GET /api/awface/journeys/{journeyId}/completion`
- `POST /api/awface/facetec/v9/3d/initialize`
- `POST /api/awface/facetec/v9/3d/session-token`
- `POST /api/awface/facetec/v9/3d/liveness`
- `POST /api/awface/facetec/v10/3d/process-request`
- `POST /api/awface/facetec/3d/process-request` (alias legado V10)

A sessão devolvida ao navegador contém apenas dados públicos do Tenant (nome, termos, privacidade e
cores) e o nome do usuário; CPF, data de nascimento, credenciais do provedor e segredos de webhook
nunca saem da API.

## Fluxo de conclusão interno

A entrega ao Tenant nasce do retorno do SDK FaceTec:

- `codID = 200`: o AWFace consulta `/facecaptcha/service/captcha/document/result`, persiste o resultado enriquecido, armazena a face frontal quando disponível e enfileira o `UrlCallback` do Tenant.
- `codID = 300.1`: o AWFace registra o retorno do SDK, enfileira o `UrlCallback` e mantém a jornada apta para nova tentativa.
- `codID = 300.2`: o AWFace registra o retorno do SDK, enfileira o `UrlCallback` e finaliza a jornada como falha/bloqueio.
- Outros `codID`: registrados em `awface_liveness_attempt_history` (`SDK_UNKNOWN_CODID`), sem finalizar a jornada nem notificar o Tenant.

O processamento continua mesmo que o usuário feche a aba durante o envio, e submissões repetidas do
SDK (mesma appkey e mesmo conteúdo) recebem a resposta já processada, sem nova chamada à Certiface.

## Estado interno de conclusão

O frontend consulta:

```http
GET /api/awface/journeys/{journeyId}/completion
X-AWFace-Journey-Token: <token da sessão>
```

A consulta ocorre imediatamente ao abrir `/journey-completion` e depois a cada
5 segundos enquanto o status for `PENDING`.

- `PENDING`: aguardando o processamento ou a primeira entrega ao Tenant.
- `SUCCESS`: webhook do Tenant entregue com HTTP 2xx.
- `DEFERRED`: prova de vida concluída; a entrega ao Tenant falhou nas primeiras tentativas e segue em retentativa automática.
- `FAILED`: erro terminal do provedor (300.2) ou todas as tentativas de entrega ao Tenant esgotadas.
