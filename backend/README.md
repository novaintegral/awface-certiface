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

## Endpoints principais

- `POST /api/awface/admin/login`
- `GET /api/awface/admin/tenants`
- `POST /api/awface/admin/tenants`
- `PATCH /api/awface/admin/tenants/{tenantId}/status`
- `POST /api/awface/journeys`
- `POST /api/awface/journeys/{journeyId}/consent`
- `POST /api/awface/journeys/{journeyId}/appkey`
- `POST /api/awface/facetec/3d/process-request`
- `POST /webhookliveness`

O Angular em desenvolvimento aponta para `http://localhost:5000` por `environment.awfaceApiUrl`.
