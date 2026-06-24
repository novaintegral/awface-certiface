using AWFace.Api.Domain;

namespace AWFace.Api.Configuration;

public sealed class AwfaceOptions
{
    public string Schema { get; set; } = "public";
    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
    public int LivenessAppkeyLifetimeMinutes { get; set; } = 20;
    public LivenessEngine LivenessEngine { get; set; } = LivenessEngine.V10;
    public FaceStorageOptions FaceStorage { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
}

public sealed class FaceStorageOptions
{
    public string RootPath { get; set; } = "storage/faces";
    public string EncryptionKey { get; set; } = "dev-only-change-this-face-storage-key";
}

public sealed class AdminOptions
{
    public string Email { get; set; } = "admin@awface.local";
    public string Password { get; set; } = "Awface@123";
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
