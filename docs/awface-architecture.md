# AWFace architecture notes

## Assisted journey

The assisted/demo flow opens AWFace at `/journey-start` and allows manual input through the browser:

- `token` or `integrationToken`
- `journeyType`
- `cpf`
- `nome` or `fullName`
- `nascimento` or `birthDate`
- `idExternoCliente` or `externalClientId`

The Angular app validates the input, resolves tenant customization through the AWFace API, records consent, creates the Certiface appkey, and then starts the existing FaceTec V10 flow.

This route is intended for demos and assisted operation. CPF, name and birth date should not be used in browser query strings for host-to-host production integration.

## Autonomous host launch

The production-style host integration starts server-to-server:

1. The Host application calls `POST /api/awface/journey-launches` with tenant token, subject data, journey type and optional host metadata.
2. AWFace creates the journey, stores only a hash of the launch token, and returns a short-lived `launchUrl`.
3. The Host redirects the user to `/#/journey-launch?token=...`.
4. Angular consumes the token, stores the resolved journey locally and displays the user consent screen.
5. After explicit user consent, Angular registers the decision, creates the Certiface appkey through AWFace.Api and redirects to `/#/journey`.
6. After successful liveness, AWFace calls the Tenant `UrlCallback` and the user is sent to `/#/journey-completion`.

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

- `POST /api/awface/journeys`
- `POST /api/awface/journey-launches`
- `POST /api/awface/journey-launches/{launchToken}/consume`
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `POST /api/awface/facetec/3d/process-request`
- `GET /api/awface/admin/tenants`
- `POST /api/awface/admin/tenants`
- `PATCH /api/awface/admin/tenants/{tenantId}/status`
- `POST /webhookliveness`

The Angular service uses these endpoints first and still falls back to localStorage for local development when the backend is not available.

## Certiface flow

Based on the Certiface API Global documentation:

1. Get provider credential token with `POST /facecaptcha/service/captcha/credencial`.
2. Create an appkey with `POST /facecaptcha/service/captcha/appkey`.
3. Start FaceTec V10 using the existing SDK assets.
4. Process 3D SDK requests through AWFace with `POST /api/awface/facetec/3d/process-request`, which proxies Certiface `POST /facecaptcha/service/captcha/3d/process-request`.
5. Receive provider completion webhook at AWFace.
6. Query final result with `POST /facecaptcha/service/captcha/document/result`.
7. Store the final result in Postgres.
8. Forward a signed notification to the tenant `UrlCallback` using `SecureCallback`.

## Admin

The master admin login is intentionally hardcoded for this first slice:

- email: `admin@awface.local`
- password: `Awface@123`

Replace this with persisted users, password hashing, MFA and role-based access before production use.
