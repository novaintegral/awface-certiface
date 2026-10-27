using System.Security.Cryptography;
using System.Text;
using AWFace.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Security;

/// <summary>
/// Criptografia de dados pessoais e segredos persistidos no banco.
/// Formato: <c>enc:v1:</c> + base64(nonce[12] | tag[16] | ciphertext) com AES-256-GCM.
/// Valores legados (somente base64, versões até 1.0.x) continuam legíveis e são
/// recriptografados pelo <see cref="AWFace.Api.Data.SensitiveDataMigrator"/>.
/// </summary>
public sealed class SensitiveDataProtector
{
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _hashKey;

    public SensitiveDataProtector(IOptions<AwfaceOptions> options)
    {
        var masterKey = KeyMaterial.Resolve(options.Value.Security.DataEncryptionKey, "Awface:Security:DataEncryptionKey");
        _encryptionKey = KeyMaterial.Derive(masterKey, "awface-pii-encryption-v1");
        _hashKey = KeyMaterial.Derive(masterKey, "awface-pii-hash-v1");
    }

    /// <summary>HMAC-SHA256 com chave secreta: não é reversível por força bruta sem a chave.</summary>
    public string Hash(string value)
    {
        var bytes = HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(Normalize(value)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string Protect(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(_encryptionKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);
        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string value)
    {
        if (!IsProtected(value))
        {
            return UnprotectLegacy(value);
        }

        var payload = Convert.FromBase64String(value[Prefix.Length..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Dado criptografado inválido.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public static bool IsProtected(string? value)
    {
        return value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Formato das versões 1.0.x (somente base64). Mantido apenas para leitura e migração.</summary>
    public static string UnprotectLegacy(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}
