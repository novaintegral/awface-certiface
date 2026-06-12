# AWFace architecture notes

## Public journey

The host application opens AWFace at `/` with query parameters:

- `token` or `integrationToken`
- `journeyType`
- `cpf`
- `nome` or `fullName`
- `nascimento` or `birthDate`
- `idExternoCliente` or `externalClientId`

The Angular app validates the input, resolves tenant customization through the AWFace API, records consent, creates the Certiface appkey, and then starts the existing FaceTec V10 flow.

Sensitive data must be sent by the host through a backend-created short-lived launch token in production. Query parameters are supported in the current UI to keep local testing simple, but CPF, name and birth date should not remain in browser history in the final host integration.

## Expected AWFace API

- `POST /api/awface/journeys`
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `GET /api/awface/admin/tenants`
- `POST /api/awface/admin/tenants`
- `PATCH /api/awface/admin/tenants/{tenantId}/status`
- `POST /webhookliveness`

The current Angular service uses these endpoints first and falls back to localStorage for local development when the backend is not available.

## Certiface flow

Based on the Certiface API Global documentation:

1. Get provider credential token with `POST /facecaptcha/service/captcha/credencial`.
2. Create an appkey with `POST /facecaptcha/service/captcha/appkey`.
3. Start FaceTec V10 using the existing SDK assets.
4. Process 3D SDK requests with `POST /facecaptcha/service/captcha/3d/process-request`.
5. Receive provider completion webhook at AWFace.
6. Query final result with `POST /facecaptcha/service/captcha/document/result`.
7. Store the final result in Postgres.
8. Forward a signed notification to the tenant `UrlCallback` using `SecureCallback`.

## Admin

The master admin login is intentionally hardcoded for this first slice:

- email: `admin@awface.local`
- password: `Awface@123`

Replace this with persisted users, password hashing, MFA and role-based access before production use.
