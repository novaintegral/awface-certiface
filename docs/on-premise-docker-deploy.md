# Deploy on-premise com Docker Compose

Este compose sobe:

- `awface-front`: Angular servido por Nginx na porta `5580`.
- `awface-api`: AWFace.Api .NET 9 atrás do Nginx em `/api` (porta `5500` publicada apenas em `127.0.0.1` por padrão).
- `awface-db`: PostgreSQL 16 (porta `5432` publicada apenas em `127.0.0.1` por padrão).

Para expor a API ou o banco em outra interface, defina `AWFACE_API_BIND` ou
`POSTGRES_BIND` (por exemplo `0.0.0.0`) somente com firewall restringindo a origem.

## Preparar configuração

Copie o arquivo de exemplo:

```bash
cp .env.example .env
```

Em Windows PowerShell:

```powershell
Copy-Item .env.example .env
```

O arquivo `.env` contém segredos e está no `.gitignore`: nunca o versione.

Variáveis obrigatórias (o `docker compose` recusa subir sem elas e a API não
inicia fora de `Development` com valores vazios, curtos ou de exemplo):

- `POSTGRES_PASSWORD`
- `AWFACE_DATA_ENCRYPTION_KEY`: chave mestra AES-256-GCM dos dados pessoais
  (CPF, nome, nascimento) e dos segredos de Tenant.
- `AWFACE_FACE_STORAGE_ENCRYPTION_KEY`: chave AES-256-GCM das imagens de face.

Gere chaves fortes com:

```bash
openssl rand -base64 32
```

Ajuste também:

- `AWFACE_FRONTEND_BASE_URL`
- `AWFACE_LIVENESS_APPKEY_LIFETIME_MINUTES`
- `AWFACE_FACE_STORAGE_ROOT_PATH`
- `AWFACE_ADMIN_EMAIL` e `AWFACE_ADMIN_PASSWORD` (somente para criar o primeiro administrador)

### Administrador

As credenciais administrativas ficam na tabela `awface_admin_user`, com senha
em PBKDF2-SHA256. Na primeira inicialização sem administradores, a API cria o
usuário a partir de `AWFACE_ADMIN_EMAIL`/`AWFACE_ADMIN_PASSWORD` (senha com
12+ caracteres). Após o primeiro acesso, remova `AWFACE_ADMIN_PASSWORD` do `.env`.

O login emite um token assinado (padrão de 60 minutos) exigido por todas as
rotas `/api/awface/admin/*`. O endpoint de login aceita 10 tentativas por
minuto por IP.

### Atualização a partir da 1.0.x

1. Crie as chaves novas no `.env` (`AWFACE_DATA_ENCRYPTION_KEY` e uma nova
   `AWFACE_FACE_STORAGE_ENCRYPTION_KEY`).
2. Se as imagens existentes foram gravadas com a chave padrão antiga, informe-a
   em `AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY` para que continuem legíveis.
3. Na inicialização, a API recriptografa automaticamente CPF, nome, nascimento,
   senhas Certiface e client secrets OAuth que estavam apenas em Base64.
4. **Troque as senhas que circularam em arquivos versionados**: senha do
   Postgres, senha do administrador e as credenciais Certiface de cada Tenant.

Em produção, `AWFACE_FRONTEND_BASE_URL` deve ser a URL que os usuários acessam, por exemplo:

```env
AWFACE_FRONTEND_BASE_URL=https://awface.suaempresa.com.br
```

## Script de inicialização (Windows)

Para testar na própria máquina, use o script, que prepara o `.env`, sobe os
containers e aguarda a API ficar saudável:

```powershell
.\scripts\awface.ps1 start      # prepara o .env, constrói e sobe tudo
.\scripts\awface.ps1 stop       # encerra (dados em docker-data são preservados)
.\scripts\awface.ps1 restart
.\scripts\awface.ps1 status
.\scripts\awface.ps1 logs awface-api
.\scripts\awface.ps1 check      # prepara o .env e valida o compose sem subir
```

No Prompt de Comando: `awface start` / `awface stop` (arquivo `awface.cmd` na raiz).

Ao preparar o `.env`, o script:

- cria o `.env` a partir do `.env.example`, se não existir, e acrescenta variáveis novas sem alterar as existentes;
- gera `AWFACE_DATA_ENCRYPTION_KEY`, `AWFACE_FACE_STORAGE_ENCRYPTION_KEY` e `POSTGRES_PASSWORD` (esta só quando o banco ainda não existe) quando vazias;
- preserva a chave padrão da 1.0.x em `AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY` quando já há imagens gravadas;
- gera uma senha forte para o administrador inicial quando a atual é fraca e a exibe uma única vez;
- sincroniza `AWFACE_VERSION` com o arquivo `VERSION` e grava uma cópia `.env.bak-<data>` antes de alterar.

Antes de subir, o script também verifica as portas do host. Se a porta do
banco (5432) ou da API (5500) estiver ocupada — por exemplo, por um PostgreSQL
instalado no Windows ou por uma faixa reservada pelo Hyper-V — ele escolhe a
próxima porta livre e grava em `POSTGRES_PORT`/`AWFACE_API_PORT`. Os containers
continuam se comunicando pelas portas internas, então a aplicação não é afetada.
Se a porta do frontend (5580) estiver ocupada, o script para e indica o processo
que a utiliza.

Requer Docker Desktop em execução.

## Subir ambiente

```bash
docker compose up -d --build
```

Acesse:

- Frontend: `http://localhost:5580`
- API health: `http://localhost:5580/health`
- API direta (somente no próprio servidor): `http://127.0.0.1:5500/health`

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

Para rotacionar a chave das imagens, mova a chave atual para
`AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY` e defina uma nova em
`AWFACE_FACE_STORAGE_ENCRYPTION_KEY`. Novas imagens usam a chave nova; as
antigas continuam legíveis pela chave anterior.

Preserve também `AWFACE_DATA_ENCRYPTION_KEY`: sem ela, os dados pessoais das
jornadas não podem ser lidos.

Os logs da API são removidos após `AWFACE_LOG_RETENTION_DAYS` dias (padrão 30)
e não registram CPF, nome nem data de nascimento.

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

## Cabeçalhos de segurança

O Nginx envia `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`,
`Permissions-Policy` (câmera e geolocalização apenas para o próprio domínio) e
`Strict-Transport-Security`. A Content Security Policy está em modo
`Report-Only` para homologação com os SDKs FaceTec; após validar no navegador
(console sem violações), troque o cabeçalho para `Content-Security-Policy`.

Publique o AWFace sempre atrás de HTTPS. O Nginx sobrescreve
`X-Forwarded-For` com o IP da conexão; se houver outro proxy TLS na frente,
ajuste esse cabeçalho para preservar o IP real do usuário.

## Parar ambiente

```bash
docker compose down
```

Para parar sem perder dados, não use `-v`.
