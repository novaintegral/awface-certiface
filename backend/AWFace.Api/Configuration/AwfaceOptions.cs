using AWFace.Api.Domain;

namespace AWFace.Api.Configuration;

public sealed class AwfaceOptions
{
    public string Schema { get; set; } = "public";
    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
    public int LivenessAppkeyLifetimeMinutes { get; set; } = 20;
    public LivenessEngine LivenessEngine { get; set; } = LivenessEngine.V10;
    public bool TrustForwardedHeaders { get; set; }
    public SecurityOptions Security { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
    public FaceStorageOptions FaceStorage { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
}

public sealed class SecurityOptions
{
    /// <summary>
    /// Chave mestra para criptografia AES-256-GCM dos dados pessoais (CPF, nome, nascimento)
    /// e dos segredos de Tenant. Aceita 32 bytes em base64 (recomendado) ou uma frase longa.
    /// </summary>
    public string DataEncryptionKey { get; set; } = "";

    /// <summary>
    /// Chave HMAC para assinar tokens de administrador e de sessão de jornada.
    /// Quando vazia, é derivada de <see cref="DataEncryptionKey"/>.
    /// </summary>
    public string TokenSigningKey { get; set; } = "";

    public int AdminTokenLifetimeMinutes { get; set; } = 60;
    public int JourneyTokenLifetimeMinutes { get; set; } = 120;
}

public sealed class WebhookOptions
{
    public int MaxAttempts { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Quantidade de tentativas com falha a partir da qual a tela de conclusão deixa de aguardar
    /// e informa ao usuário que o resultado será entregue automaticamente.
    /// </summary>
    public int DeferCompletionAfterAttempts { get; set; } = 3;

    /// <summary>
    /// Somente para ambiente local com a API em container: host usado no lugar de localhost/127.0.0.1
    /// nas chamadas ao Tenant (ex.: host.docker.internal). Vazio desativa.
    /// </summary>
    public string LocalhostAlias { get; set; } = "";
}

public sealed class FaceStorageOptions
{
    public string RootPath { get; set; } = "storage/faces";
    public string EncryptionKey { get; set; } = "";

    /// <summary>Chaves antigas aceitas apenas para leitura, permitindo a rotação de <see cref="EncryptionKey"/>.</summary>
    public string[] PreviousEncryptionKeys { get; set; } = [];
}

public sealed class AdminOptions
{
    /// <summary>
    /// Administrador inicial. Usado apenas para criar o primeiro usuário em
    /// awface_admin_user quando a tabela estiver vazia; a senha é armazenada com PBKDF2.
    /// </summary>
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class CertifaceOptions
{
    public string BaseUrl { get; set; } = "https://hml.certiface.com.br";
    public string ResultBaseUrl { get; set; } = "https://hml.certiface.com.br:8443";
}
