using System.Security.Cryptography;
using System.Text;
using AWFace.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Services;

public sealed class FaceAssetStorage
{
    private const string EncryptionAlgorithm = "AES-256-GCM";
    private static readonly byte[] EncryptedFileMagic = Encoding.ASCII.GetBytes("AWFACEFACE1");
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IWebHostEnvironment _environment;
    private readonly FaceStorageOptions _options;

    public FaceAssetStorage(IWebHostEnvironment environment, IOptions<AwfaceOptions> options)
    {
        _environment = environment;
        _options = options.Value.FaceStorage;
    }

    public async Task<StoredFaceAsset> SaveFrontalFaceAsync(Guid tenantId, Guid journeyId, string faceBase64, CancellationToken cancellationToken)
    {
        var bytes = DecodeImageBase64(faceBase64);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var contentType = DetectContentType(bytes);
        var extension = contentType == "image/png" ? "png" : "jpg";
        var encryptedBytes = Encrypt(bytes);

        var storageKey = Path.Combine(
                tenantId.ToString("N"),
                journeyId.ToString("N"),
                $"frontal-{sha256[..16]}.{extension}.enc"
            )
            .Replace('\\', '/');

        var fullPath = GetSafeFullPath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, encryptedBytes, cancellationToken);

        return new StoredFaceAsset(storageKey, contentType, sha256, bytes.LongLength, EncryptionAlgorithm);
    }

    public async Task<StoredFaceAssetContent?> ReadAsync(string storageKey, string contentType, CancellationToken cancellationToken)
    {
        var fullPath = GetSafeFullPath(storageKey);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (IsEncrypted(bytes))
        {
            var decryptedBytes = Decrypt(bytes);
            return IsSupportedImage(decryptedBytes)
                ? new StoredFaceAssetContent(decryptedBytes, DetectContentType(decryptedBytes))
                : null;
        }

        // Backward compatibility for assets saved before encryption was enabled.
        if (IsSupportedImage(bytes))
        {
            return new StoredFaceAssetContent(bytes, DetectContentType(bytes));
        }

        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            var repairedBytes = DecodeImageBase64(text);
            return new StoredFaceAssetContent(repairedBytes, DetectContentType(repairedBytes));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string GetSafeFullPath(string storageKey)
    {
        var root = GetRootPath();
        var fullPath = Path.GetFullPath(Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Storage key de face inválida.");
        }

        return fullPath;
    }

    private string GetRootPath()
    {
        var rootPath = string.IsNullOrWhiteSpace(_options.RootPath) ? "storage/faces" : _options.RootPath;
        var root = Path.IsPathRooted(rootPath)
            ? Path.GetFullPath(rootPath)
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, rootPath));

        Directory.CreateDirectory(root);
        return root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    private static string NormalizeBase64(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        normalized = normalized.Replace("\\/", "/");
        var commaIndex = normalized.IndexOf(',');
        if (normalized.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            normalized = normalized[(commaIndex + 1)..];
        }

        normalized = normalized
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace(" ", string.Empty)
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = normalized.Length % 4;
        return padding == 0 ? normalized : normalized.PadRight(normalized.Length + 4 - padding, '=');
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var key = GetEncryptionKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var encrypted = new byte[EncryptedFileMagic.Length + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(EncryptedFileMagic, 0, encrypted, 0, EncryptedFileMagic.Length);
        Buffer.BlockCopy(nonce, 0, encrypted, EncryptedFileMagic.Length, NonceSize);
        Buffer.BlockCopy(tag, 0, encrypted, EncryptedFileMagic.Length + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, encrypted, EncryptedFileMagic.Length + NonceSize + TagSize, ciphertext.Length);
        return encrypted;
    }

    private byte[] Decrypt(byte[] encrypted)
    {
        var key = GetEncryptionKey();
        var offset = EncryptedFileMagic.Length;
        var nonce = encrypted.AsSpan(offset, NonceSize);
        offset += NonceSize;
        var tag = encrypted.AsSpan(offset, TagSize);
        offset += TagSize;
        var ciphertext = encrypted.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private byte[] GetEncryptionKey()
    {
        if (string.IsNullOrWhiteSpace(_options.EncryptionKey))
        {
            throw new InvalidOperationException("Awface:FaceStorage:EncryptionKey não configurada.");
        }

        var value = _options.EncryptionKey.Trim();
        try
        {
            var keyBytes = Convert.FromBase64String(value);
            if (keyBytes.Length is 16 or 24 or 32)
            {
                return keyBytes;
            }
        }
        catch (FormatException)
        {
            // Treat non-base64 values as passphrases and derive a 256-bit key.
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static bool IsEncrypted(byte[] bytes)
    {
        return bytes.Length > EncryptedFileMagic.Length + NonceSize + TagSize
            && bytes.AsSpan(0, EncryptedFileMagic.Length).SequenceEqual(EncryptedFileMagic);
    }

    private static byte[] DecodeImageBase64(string value)
    {
        var bytes = Convert.FromBase64String(NormalizeBase64(value));
        if (!IsSupportedImage(bytes))
        {
            throw new InvalidOperationException("A face frontal retornada pela Certiface não possui assinatura JPEG ou PNG válida.");
        }

        return bytes;
    }

    private static string DetectContentType(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return "image/jpeg";
    }

    private static bool IsSupportedImage(byte[] bytes)
    {
        return IsJpeg(bytes) || IsPng(bytes);
    }

    private static bool IsJpeg(byte[] bytes)
    {
        return bytes.Length >= 3
            && bytes[0] == 0xFF
            && bytes[1] == 0xD8
            && bytes[2] == 0xFF;
    }

    private static bool IsPng(byte[] bytes)
    {
        return bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47;
    }
}

public sealed record StoredFaceAsset(string StorageKey, string ContentType, string Sha256, long SizeBytes, string EncryptionAlgorithm);

public sealed record StoredFaceAssetContent(byte[] Bytes, string ContentType);
