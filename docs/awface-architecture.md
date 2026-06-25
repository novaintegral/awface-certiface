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
6. After successful SDK submission, the user is sent to `/#/journey-completion` while AWFace waits for the Certiface terminal webhook.
7. Certiface posts `Status` and `Appkey` to `/api/awface/webhooks/certiface`.
8. AWFace queries `document/result`, persists the final result and calls the Tenant `UrlCallback`.

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

The Angular service uses these endpoints first and still falls back to localStorage for local development when the backend is not available.

## Certiface flow

Based on the Certiface API Global documentation:

1. Get provider credential token with `POST /facecaptcha/service/captcha/credencial`.
2. Create an appkey with `POST /facecaptcha/service/captcha/appkey`.
3. Start FaceTec V9 or V10 according to the engine persisted in the journey.
4. Route all SDK requests through the engine-specific AWFace endpoints.
5. Redirect the browser immediately to `/journey-completion`.
6. Wait for the provider terminal webhook at `POST /api/awface/webhooks/certiface`.
7. Query the final result with `POST /facecaptcha/service/captcha/document/result` only after the provider reports `Completo` or `Erro`.
8. Store the final result in Postgres.
9. Extract `fotos.facecaptcha.frontal`, save the face image encrypted with AES-256-GCM in AWFace persistent filesystem storage, and store only the asset metadata in Postgres.
10. Forward the result to the tenant `UrlCallback`, optionally authenticated with OAuth2 Client Credentials.
11. Expose the final callback state through `GET /api/awface/journeys/{journeyId}/completion`.

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

`SUCCESS` is returned only after provider processing is accepted and the
Tenant callback responds with HTTP 2xx. Provider terminal errors or Tenant
callback delivery failures produce `FAILED`.

The detailed implementation record is available in
[`2026-06-24-engines-completion-provider-webhook.md`](./2026-06-24-engines-completion-provider-webhook.md).

## Admin

The master admin login is intentionally hardcoded for this first slice:

- email: `admin@awface.local`
- password: `Awface@123`

Replace this with persisted users, password hashing, MFA and role-based access before production use.
