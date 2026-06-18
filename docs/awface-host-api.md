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

Também é aceito via query string:

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

Quando a prova de vida é concluída com sucesso, o AWFace envia um `POST` para o `UrlCallback` configurado no Tenant.

Header enviado:

```http
X-AWFace-SecureCallback: <secureCallbackToken-do-tenant>
```

Payload:

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

## Endpoints internos do fluxo AWFace

Os endpoints abaixo existem, mas não devem ser chamados diretamente pela aplicação Host em produção:

- `POST /api/awface/journey-launches/{launchToken}/consume`
- `POST /api/awface/journeys`
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `GET /api/awface/journeys/{journeyId}/completion`
- `POST /api/awface/facetec/3d/process-request`
- `POST /webhookliveness`

Esses endpoints são usados pelo frontend AWFace, pelo SDK FaceTec ou por fluxos legados.
