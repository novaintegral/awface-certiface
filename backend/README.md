# AWFace.Api

Backend .NET 9 para centralizar os acessos a Postgres, Certiface e callbacks da jornada AWFace.

## Configuração local

Para desenvolvimento, execute com `ASPNETCORE_ENVIRONMENT=Development`. O arquivo
`appsettings.Development.json` traz a connection string local e chaves
**exclusivas de desenvolvimento**. Fora de `Development`, a API se recusa a
iniciar sem `Awface:Security:DataEncryptionKey` e `Awface:FaceStorage:EncryptionKey`
fortes (veja `docs/on-premise-docker-deploy.md`).

```text
Host=localhost;Port=5432;Database=awface;Username=postgres;Password=postgres;Search Path=public
```

Se sua senha local do usuário `postgres` for diferente, sobrescreva por variável de ambiente:

```powershell
$env:ConnectionStrings__Awface="Host=localhost;Port=5432;Database=awface;Username=postgres;Password=SUA_SENHA;Search Path=public;Include Error Detail=true"
```

Para criar o primeiro administrador local:

```powershell
$env:Awface__Admin__Email="admin@awface.local"
$env:Awface__Admin__Password="uma-senha-local-forte"
```

As tabelas esperadas são as do script `docs/awface-postgres-schema.sql`.

## Executar

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project backend/AWFace.Api/AWFace.Api.csproj --urls http://localhost:5000
```

Health check:

```text
GET http://localhost:5000/health
```

Versão da release em execução:

```text
GET http://localhost:5000/version
GET http://localhost:5000/api/awface/version
```

## Logs

A API grava logs em arquivo por padrão:

```text
backend/AWFace.Api/logs/awface-api-YYYYMMDD.log
```

O nível mínimo, o diretório e a retenção (`RetentionDays`, padrão 30) podem ser alterados em `backend/AWFace.Api/appsettings.json`, na seção `Logging:File`. Os logs não registram CPF, nome nem data de nascimento.

## Endpoints

A referência dos endpoints públicos para aplicações Host está em:

```text
docs/awface-host-api.md
```

Endpoints públicos para Host:

- `POST /api/awface/journey-launches`
- `GET /api/awface/journeys/{journeyId}/result`
- `GET /api/awface/journeys/{journeyId}/face-image`
- `GET /api/awface/journeys/{journeyId}/face-image/file`

Endpoints administrativos (exigem `Authorization: Bearer <token>` obtido no login, exceto o próprio login):

- `POST /api/awface/admin/login`
- `GET /api/awface/admin/me`
- `GET /api/awface/admin/tenants`
- `POST /api/awface/admin/tenants`
- `PATCH /api/awface/admin/tenants/{tenantId}/status`

Endpoints internos da jornada (exigem `X-AWFace-Journey-Token`, emitido no consumo do `launchToken`):

- `POST /api/awface/journeys` (jornada assistida; emite o token)
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `GET /api/awface/journeys/{journeyId}/completion`
- `POST /api/awface/facetec/v10/3d/process-request`
- `POST /api/awface/facetec/3d/process-request` (alias legado V10)
- `POST /api/awface/facetec/v9/3d/initialize`
- `POST /api/awface/facetec/v9/3d/session-token`
- `POST /api/awface/facetec/v9/3d/liveness`

O engine utilizado por novas jornadas é definido por `Awface:LivenessEngine`
ou pela variável `AWFACE_LIVENESS_ENGINE` no Docker Compose. Valores aceitos:
`V9` e `V10`. O valor é persistido na jornada para que alterações posteriores
de ambiente não modifiquem processos já iniciados.

O resultado do SDK é processado no próprio retorno do FaceTec e o webhook do
Tenant é entregue pela fila `awface_callback_outbox` (serviço `CallbackDispatcher`),
com retentativa exponencial e assinatura HMAC. Submissões repetidas do SDK são
deduplicadas em `awface_sdk_request`.

O Angular em desenvolvimento aponta para `http://localhost:5000` por `environment.awfaceApiUrl`.

## Releases

O arquivo `VERSION` na raiz é a fonte única da versão SemVer do AWFace.
Consulte `docs/release-management.md` para preparar, validar, etiquetar e
publicar uma release.
