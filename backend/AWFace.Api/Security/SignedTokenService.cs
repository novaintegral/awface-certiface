using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AWFace.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Security;

/// <summary>
/// Tokens compactos assinados com HMAC-SHA256: <c>awf1.{payload}.{assinatura}</c> (base64url).
/// Usados para a sessão do administrador e para a sessão da jornada no navegador.
/// </summary>
public sealed class SignedTokenService
{
    public const string AdminPurpose = "admin";
    public const string JourneyPurpose = "journey";
    private const string Version = "awf1";

    private readonly byte[] _signingKey;

    public SignedTokenService(IOptions<AwfaceOptions> options)
    {
        var security = options.Value.Security;
        _signingKey = string.IsNullOrWhiteSpace(security.TokenSigningKey)
            ? KeyMaterial.Derive(KeyMaterial.Resolve(security.DataEncryptionKey, "Awface:Security:DataEncryptionKey"), "awface-token-signing-v1")
            : KeyMaterial.Resolve(security.TokenSigningKey, "Awface:Security:TokenSigningKey");
    }

    public IssuedToken Issue(string purpose, string subject, TimeSpan lifetime)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var payload = new TokenPayload(purpose, subject, expiresAt.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"));
        var payloadSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signatureSegment = Base64Url(Sign($"{Version}.{payloadSegment}"));
        return new IssuedToken($"{Version}.{payloadSegment}.{signatureSegment}", expiresAt);
    }

    public string? Validate(string? token, string purpose)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Version)
        {
            return null;
        }

        byte[] providedSignature;
        byte[] payloadBytes;
        try
        {
            providedSignature = FromBase64Url(parts[2]);
            payloadBytes = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expectedSignature = Sign($"{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return null;
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || payload.Purpose != purpose
            || string.IsNullOrWhiteSpace(payload.Subject)
            || DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt) <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return payload.Subject;
    }

    private byte[] Sign(string value)
    {
        return HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(value));
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(normalized);
    }

    private sealed record TokenPayload(
        [property: JsonPropertyName("p")] string Purpose,
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("exp")] long ExpiresAt,
        [property: JsonPropertyName("jti")] string Id
    );
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);
