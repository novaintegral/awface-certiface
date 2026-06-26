# Changelog

Todas as mudanças relevantes do AWFace serão documentadas neste arquivo.

O formato segue [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e o projeto utiliza [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [Não publicado]

### Adicionado

- Espaço reservado para mudanças da próxima release.

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

[Não publicado]: https://github.com/novaintegral/awface-certiface/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/novaintegral/awface-certiface/releases/tag/v1.0.0
