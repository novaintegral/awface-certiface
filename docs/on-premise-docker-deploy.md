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
- `AWFACE_FACE_STORAGE_ROOT_PATH`
- `AWFACE_FACE_STORAGE_ENCRYPTION_KEY`
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

As imagens de face capturadas nas provas de vida ficam em:

```text
./docker-data/awface-storage/faces
```

Os arquivos neste diretório são criptografados em repouso com AES-256-GCM. Preserve e proteja `AWFACE_FACE_STORAGE_ENCRYPTION_KEY`; sem essa chave, imagens já gravadas não poderão ser descriptografadas.

Não remova `./docker-data/postgres` nem `./docker-data/awface-storage` se quiser preservar dados e evidências biométricas entre recriações de container.

## Banco de dados

Na primeira inicialização, o Postgres executa:

```text
docker/postgres/initdb/01-awface-schema.sql
```

Esse script cria tipos, tabelas e índices necessários. O script só roda automaticamente quando o volume de dados do Postgres ainda está vazio.

Em bancos já existentes, a `AWFace.Api` aplica migrations versionadas durante a inicialização e registra cada execução em:

```text
awface_schema_migration
```

Os scripts versionados também ficam disponíveis para auditoria ou execução manual em:

```text
docker/postgres/migrations
```

## Parar ambiente

```bash
docker compose down
```

Para parar sem perder dados, não use `-v`.
