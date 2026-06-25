# AWFace.Api

Backend .NET 9 para centralizar os acessos a Postgres, Certiface e callbacks da jornada AWFace.

## Banco local

Configuração padrão em `backend/AWFace.Api/appsettings.json`:

```text
Host=localhost;Port=5432;Database=awface;Username=postgres;Password=postgres;Search Path=public
```

Se sua senha local do usuário `postgres` for diferente, sobrescreva por variável de ambiente:

```powershell
$env:ConnectionStrings__Awface="Host=localhost;Port=5432;Database=awface;Username=postgres;Password=SUA_SENHA;Search Path=public;Include Error Detail=true"
```

As tabelas esperadas são as do script `docs/awface-postgres-schema.sql`.

## Executar

```powershell
dotnet run --project backend/AWFace.Api/AWFace.Api.csproj --urls http://localhost:5000
```

Health check:

```text
GET http://localhost:5000/health
```

## Logs

A API grava logs em arquivo por padrão:

```text
backend/AWFace.Api/logs/awface-api-YYYYMMDD.log
```

O nível mínimo e o diretório podem ser alterados em `backend/AWFace.Api/appsettings.json`, na seção `Logging:File`.

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

Endpoints administrativos e internos:

- `POST /api/awface/admin/login`
- `GET /api/awface/admin/tenants`
- `POST /api/awface/admin/tenants`
- `PATCH /api/awface/admin/tenants/{tenantId}/status`
- `POST /api/awface/journeys`
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
- `POST /api/awface/webhooks/certiface`
- `POST /webhookliveness` (alias legado)

O webhook da Certiface recebe `Status` e `Appkey`. Somente os status
`Completo` e `Erro` finalizam a jornada. Após essa notificação, o AWFace
consulta `document/result`, persiste o resultado e dispara o webhook
configurado no Tenant. Os endpoints de liveness V9/V10 não consultam mais
`document/result` diretamente.

Em homologação, a configuração
`Awface:Homologation:AcceptNonProcessedResultAfterCompleteNotification=true`
permite continuar quando a Certiface notificar `Completo`, mas
`document/result` ainda responder `Não processado`. Esse recurso deve
permanecer desativado em produção.

O Angular em desenvolvimento aponta para `http://localhost:5000` por `environment.awfaceApiUrl`.
