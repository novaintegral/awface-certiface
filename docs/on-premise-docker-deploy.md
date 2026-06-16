# Deploy on-premise com Docker Compose

Este compose sobe:

- `awface-front`: Angular servido por Nginx na porta `8080`.
- `awface-api`: AWFace.Api .NET 9 na porta `5000` e também atrás do Nginx em `/api`.
- `awface-db`: PostgreSQL 16 na porta `5432`.

## Preparar configuração

Copie o arquivo de exemplo:

```bash
cp .env.example .env
```

Em Windows PowerShell:

```powershell
Copy-Item .env.example .env
```

Ajuste principalmente:

- `POSTGRES_PASSWORD`
- `AWFACE_FRONTEND_BASE_URL`
- `AWFACE_LIVENESS_APPKEY_LIFETIME_MINUTES`
- `AWFACE_ADMIN_EMAIL`
- `AWFACE_ADMIN_PASSWORD`

Em produção, `AWFACE_FRONTEND_BASE_URL` deve ser a URL que os usuários acessam, por exemplo:

```env
AWFACE_FRONTEND_BASE_URL=https://awface.suaempresa.com.br
```

## Subir ambiente

```bash
docker compose up -d --build
```

Acesse:

- Frontend: `http://localhost:8080`
- API health: `http://localhost:8080/health`
- API direta: `http://localhost:5000/health`

## Persistência de dados

Os dados do PostgreSQL ficam fora do container em:

```text
./docker-data/postgres
```

Os logs da API ficam em:

```text
./docker-data/api-logs
```

Não remova `./docker-data/postgres` se quiser preservar os dados entre recriações de container.

## Banco de dados

Na primeira inicialização, o Postgres executa:

```text
docker/postgres/initdb/01-awface-schema.sql
```

Esse script cria tipos, tabelas e índices necessários. O script só roda automaticamente quando o volume de dados do Postgres ainda está vazio.

## Parar ambiente

```bash
docker compose down
```

Para parar sem perder dados, não use `-v`.
