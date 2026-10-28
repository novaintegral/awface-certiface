# Changelog

Todas as mudanças relevantes do AWFace serão documentadas neste arquivo.

O formato segue [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e o projeto utiliza [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [Não publicado]

### Adicionado

- Espaço reservado para mudanças da próxima release.

## [1.1.0] - 2026-10-27

Release de segurança e confiabilidade (fases 0 e 1 do estudo de código de 27/10/2026).

### Adicionado

- Script `scripts/awface.ps1` (e atalho `awface.cmd`) para iniciar, encerrar,
  reiniciar e acompanhar o ambiente Docker, preparando o `.env` com chaves fortes.
- `AWFACE_WEBHOOK_LOCALHOST_ALIAS` para testes locais com a API em Docker e o
  Host (ex.: Portal) rodando em `localhost` na máquina; comando
  `retry-webhooks` no script para reenviar webhooks pendentes.
- Log de falha do webhook passa a registrar o motivo (ex.: erro de conexão/TLS).

### Segurança

- Rotas `/api/awface/admin/*` exigem token assinado (HMAC-SHA256) emitido no login;
  administradores persistidos em `awface_admin_user` com senha PBKDF2-SHA256,
  login com limite de 10 tentativas/min por IP e `adminGuard` no frontend.
- Removidos o login de admin local no navegador e as credenciais embutidas nos
  arquivos `environment*.ts`.
- A sessão da jornada devolvida ao navegador não contém mais segredos do Tenant
  (token de integração, senha Certiface, segredo/OAuth do webhook) nem CPF e
  data de nascimento; o admin não recebe mais senhas do provedor nem client secret.
- Endpoints internos da jornada e do SDK exigem `X-AWFace-Journey-Token`.
- CPF, nome, nascimento, senhas Certiface e client secrets OAuth passam a ser
  gravados com AES-256-GCM; `cpf_hash` com HMAC-SHA256. Dados legados (Base64)
  são migrados automaticamente na inicialização.
- A API não inicia fora de `Development` com chaves ausentes, fracas ou de exemplo.
- Webhook ao Tenant assinado (`X-AWFace-Signature`, `X-AWFace-Timestamp`) e
  identificado (`X-AWFace-Event-Id` / `eventId`).
- `.env` e `deploy/env` deixam de ser versionados; `.env.example` sem segredos.
- Postgres e API publicados apenas em `127.0.0.1` por padrão; Nginx com
  cabeçalhos de segurança (CSP em modo report-only) e sem a rota `/webhookliveness`.
- Logs sem data de nascimento, identificador externo e usuário Certiface;
  corpos de erro truncados; retenção configurável (padrão 30 dias).
- Rotação da chave das imagens de face com `AWFACE_FACE_STORAGE_PREVIOUS_ENCRYPTION_KEY`.

### Corrigido

- `codID` desconhecido ou ausente não finaliza mais a jornada como concluída.
- Jornada de outro Tenant retorna `403` (antes gerava erro `500`).
- Payload inválido do SDK retorna `400` em vez de `500`.
- Mensagens com acentuação corrompida na API e na tela de bloqueio.

### Alterado

- Webhook do Tenant entregue por fila persistente (`awface_callback_outbox`) com
  retentativa exponencial (até 10 tentativas) e registro de cada tentativa.
- Submissões repetidas do SDK são deduplicadas (`awface_sdk_request`) e não
  geram nova chamada à Certiface nem webhook duplicado.
- O processamento da submissão continua mesmo se o usuário fechar a aba.
- `/completion` ganhou o estado `DEFERRED` (prova de vida concluída, entrega ao
  Host em retentativa).
- O navegador só repete o envio do SDK em falha de rede, 409, 503 ou 504.
- Erros não tratados retornam ProblemDetails com `traceId`.
- Removida a configuração sem uso `AcceptNonProcessedResultAfterCompleteNotification`.

### Atenção na atualização

- Defina `AWFACE_DATA_ENCRYPTION_KEY` e uma nova `AWFACE_FACE_STORAGE_ENCRYPTION_KEY`
  (veja `docs/on-premise-docker-deploy.md`).
- Troque as senhas que estiveram versionadas (Postgres, administrador e
  credenciais Certiface dos Tenants).
- Hosts devem passar a validar `X-AWFace-Signature` e deduplicar por `eventId`.

## [1.0.0] - 2026-06-25

### Adicionado

- Engines FaceTec V9 e V10 selecionáveis por configuração.
- Persistência do engine e histórico de appkeys por tentativa.
- Fluxo de conclusão assíncrono com polling de cinco segundos.
- Webhook de notificação terminal recebido da Certiface.
- Consulta ao resultado somente após notificação terminal do provedor.
- Webhook de conclusão do Tenant com OAuth2 Client Credentials.
- Persistência criptografada da imagem frontal.
- Deploy on-premise com frontend, API e PostgreSQL em Docker Compose.
- Controle de release SemVer unificado entre frontend, backend e Docker.

[Não publicado]: https://github.com/novaintegral/awface-certiface/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/novaintegral/awface-certiface/compare/v1.0.3...v1.1.0
[1.0.0]: https://github.com/novaintegral/awface-certiface/releases/tag/v1.0.0
