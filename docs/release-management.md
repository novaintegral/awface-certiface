# Controle de versão e releases do AWFace

## Estratégia

O AWFace usa Versionamento Semântico:

```text
MAJOR.MINOR.PATCH
```

- `MAJOR`: alteração incompatível em contrato público ou operação.
- `MINOR`: nova funcionalidade compatível.
- `PATCH`: correção compatível.

O arquivo `VERSION` é a fonte canônica. A mesma versão deve existir em:

- `package.json`;
- `package-lock.json`;
- assembly `AWFace.Api`;
- imagens Docker `awface-front` e `awface-api`;
- identificação exibida nas telas administrativas.

O comando `npm run version:verify` interrompe o build quando esses arquivos
estão divergentes.

## Preparar uma release

Para incrementar automaticamente:

```powershell
npm run release:prepare -- patch
npm run release:prepare -- minor
npm run release:prepare -- major
```

Também é possível informar uma versão explícita:

```powershell
npm run release:prepare -- 1.1.0-rc.1
```

O comando atualiza `VERSION`, `package.json` e `package-lock.json`.

Depois:

1. Mova as mudanças de `Não publicado` no `CHANGELOG.md` para a nova versão.
2. Informe a data da release.
3. Atualize os links de comparação no final do changelog.
4. Execute as validações.

```powershell
npm run version:verify
npm run build
dotnet build backend/AWFace.Api/AWFace.Api.csproj -c Release
docker compose config
docker compose build
```

5. Crie um commit exclusivo de release.
6. Crie uma tag anotada com o mesmo número:

```powershell
git tag -a v1.0.0 -m "AWFace 1.0.0"
git push origin develop-codex
git push origin v1.0.0
```

Não reutilize nem mova uma tag publicada. Uma correção deve gerar uma nova
versão `PATCH`.

## Metadados Docker

Antes de construir uma release on-premise, configure:

```env
AWFACE_VERSION=1.0.0
AWFACE_BUILD_COMMIT=e449e6c
AWFACE_BUILD_DATE=2026-06-25T12:00:00Z
```

`AWFACE_VERSION` deve ser idêntica ao arquivo `VERSION`. Os Dockerfiles
interrompem o build quando houver divergência.

As imagens resultantes são nomeadas:

```text
awface-front:1.0.0
awface-api:1.0.0
```

As labels OCI armazenam versão, commit, data e repositório de origem.

## Diagnóstico da versão instalada

Frontend administrativo:

```text
AWFace v1.0.0
```

API:

```http
GET /version
GET /api/awface/version
```

Exemplo:

```json
{
  "service": "AWFace.Api",
  "version": "1.0.0",
  "commit": "e449e6c",
  "buildDate": "2026-06-25T12:00:00Z",
  "environment": "Production"
}
```

O endpoint `GET /health` também informa `version` e `commit`.

## Política recomendada

- Desenvolvimento diário em `develop-codex`.
- Release somente a partir de um commit validado e identificável.
- Tag `vMAJOR.MINOR.PATCH` imutável.
- Uma entrada no changelog para toda mudança observável.
- Migration de banco compatível e versionada junto da release que a utiliza.
- Rollback feito pela versão anterior da imagem, nunca reconstruindo a mesma
  tag com conteúdo diferente.

