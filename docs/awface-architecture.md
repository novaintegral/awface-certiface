# AWFace architecture notes

## Assisted journey

The assisted/demo flow opens AWFace at `/journey-start` and allows manual input through the browser:

- `token` or `integrationToken`
- `journeyType`
- `cpf`
- `nome` or `fullName`
- `nascimento` or `birthDate`
- `idExternoCliente` or `externalClientId`

The Angular app validates the input, resolves tenant customization through the AWFace API, records consent, creates the Certiface appkey, and then starts the FaceTec engine selected for the journey.

This route is intended for demos and assisted operation. CPF, name and birth date should not be used in browser query strings for host-to-host production integration.

## Autonomous host launch

The production-style host integration starts server-to-server:

1. The Host application calls `POST /api/awface/journey-launches` with tenant token, subject data, journey type and optional host metadata.
2. AWFace creates the journey, stores only a hash of the launch token, and returns a short-lived `launchUrl`.
3. The Host redirects the user to `/#/journey-launch?token=...`.
4. Angular consumes the token, stores the resolved journey locally and displays the user consent screen.
5. After explicit user consent, Angular registers the decision, creates the Certiface appkey through AWFace.Api and redirects to `/#/journey`.
6. After successful SDK submission, AWFace processes the SDK result and enqueues the Tenant `UrlCallback` (outbox with retries) without waiting for a Certiface provider callback.
7. For `codID = 200`, AWFace also queries `document/result`, persists the enriched result and stores the frontal face asset before enqueuing the Tenant callback.
8. The user is sent to `/#/journey-completion`, which reflects the Tenant callback delivery state.

The `launchToken` can be reused while it is valid. AWFace only rejects invalid or expired launch tokens. The higher-priority expiration control is the liveness provider `appkey`: AWFace reuses an existing appkey only while it is inside `Awface:LivenessAppkeyLifetimeMinutes`; after that window, AWFace requests a fresh provider appkey.

Request example:

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
    "documentId": "document-789"
  }
}
```

Response example:

```json
{
  "journeyId": "00000000-0000-0000-0000-000000000000",
  "launchToken": "opaque-short-lived-token",
  "launchUrl": "http://localhost:4200/#/journey-launch?token=opaque-short-lived-token",
  "expiresAt": "2026-06-15T18:50:00Z"
}
```

## AWFace API (.NET 9)

The backend lives in `backend/AWFace.Api` and runs on .NET 9. It centralizes calls to Postgres, Certiface and tenant callbacks so browser code no longer needs provider credentials or database access.

Public Host integration endpoints are documented in [`awface-host-api.md`](./awface-host-api.md).

Main Host-facing endpoints:

- `POST /api/awface/journey-launches`
- `GET /api/awface/journeys/{journeyId}/result`
- `GET /api/awface/journeys/{journeyId}/face-image`
- Tenant `UrlCallback`, called by AWFace after successful liveness completion.

Internal browser/SDK/admin endpoints are intentionally not part of the Host contract.

Internal browser endpoints require the journey session token (`X-AWFace-Journey-Token`) issued when the launch token is consumed.

## Certiface flow

Based on the Certiface API Global documentation:

1. Get provider credential token with `POST /facecaptcha/service/captcha/credencial`.
2. Create an appkey with `POST /facecaptcha/service/captcha/appkey`.
3. Start FaceTec V9 or V10 according to the engine persisted in the journey.
4. Route all SDK requests through the engine-specific AWFace endpoints.
5. Process the SDK result immediately in AWFace.
6. For `codID = 200`, query `POST /facecaptcha/service/captcha/document/result` with the appkey, store the enriched result in Postgres, and save `fotos.facecaptcha.frontal` encrypted with AES-256-GCM in AWFace persistent filesystem storage.
7. For `codID = 300.1`, persist the SDK result, notify the Tenant and keep the journey active for retry.
8. For `codID = 300.2`, persist the SDK result, notify the Tenant and finalize the journey.
9. Forward the result to the tenant `UrlCallback`, optionally authenticated with OAuth2 Client Credentials.
10. Expose the callback delivery state through `GET /api/awface/journeys/{journeyId}/completion`.

## Liveness engine selection

New journeys use the engine configured in `Awface:LivenessEngine` or
`AWFACE_LIVENESS_ENGINE`. Supported values are `V9` and `V10`.

The selected engine is persisted in the journey. FaceTec V9 retries generate a
new appkey and preserve every issued appkey in
`awface_liveness_appkey_history`.

Internal SDK endpoints:

- V9: `/api/awface/facetec/v9/3d/initialize`
- V9: `/api/awface/facetec/v9/3d/session-token`
- V9: `/api/awface/facetec/v9/3d/liveness`
- V10: `/api/awface/facetec/v10/3d/process-request`
- V10 legacy alias: `/api/awface/facetec/3d/process-request`

## Journey completion

The SDK redirects to `/journey-completion` immediately after a successful
submission. The completion screen performs its first status request
immediately and polls every 5 seconds while the response is `PENDING`.

`SUCCESS` is returned after the Tenant callback responds with HTTP 2xx. Tenant callback delivery failures or blocking provider results produce `FAILED`.

The detailed implementation record is available in
[`2026-06-24-engines-completion-provider-webhook.md`](./2026-06-24-engines-completion-provider-webhook.md).

## Admin

Administradores ficam em `awface_admin_user` com senha PBKDF2-SHA256. O
primeiro usuário é criado a partir de `Awface:Admin:Email`/`Password`
(`AWFACE_ADMIN_EMAIL`/`AWFACE_ADMIN_PASSWORD`) quando a tabela está vazia.

`POST /api/awface/admin/login` devolve um token assinado (HMAC-SHA256) exigido
em `Authorization: Bearer` por todas as demais rotas `/api/awface/admin/*`.
O frontend guarda o token apenas no `sessionStorage` e protege as telas com
`adminGuard`. As respostas administrativas nunca incluem a senha Certiface nem
o client secret OAuth; campos de segredo vazios no cadastro mantêm o valor atual.

Pendências para fases seguintes: gestão de múltiplos usuários pela interface,
MFA e perfis de acesso.

## Segurança dos dados (1.1.0)

- CPF, nome, nascimento, senhas Certiface e client secrets OAuth são gravados
  com AES-256-GCM (`enc:v1:`), chave `Awface:Security:DataEncryptionKey`.
- `cpf_hash` usa HMAC-SHA256 com chave derivada da chave mestra.
- Dados legados em Base64 são recriptografados automaticamente na inicialização.
- A API não inicia fora de `Development` com chaves ausentes, fracas ou de exemplo.
- O navegador recebe um token de sessão da jornada (`X-AWFace-Journey-Token`)
  exigido nos endpoints internos de consentimento, appkey, conclusão e SDK.

## Confiabilidade (1.1.0)

- Webhooks do Tenant passam por `awface_callback_outbox` e são entregues pelo
  `CallbackDispatcher` (serviço em segundo plano) com retentativa exponencial e
  assinatura HMAC (`X-AWFace-Signature`).
- Submissões do SDK são deduplicadas em `awface_sdk_request` por
  appkey + conteúdo; repetições recebem a resposta armazenada.
- O processamento da submissão não depende da conexão do navegador.
- Erros não tratados retornam ProblemDetails com `traceId`.
