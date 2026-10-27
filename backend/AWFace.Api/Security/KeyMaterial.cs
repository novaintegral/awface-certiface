using System.Security.Cryptography;
using System.Text;

namespace AWFace.Api.Security;

public static class KeyMaterial
{
    /// <summary>
    /// Valores de exemplo que já circularam em arquivos versionados e nunca devem ser aceitos fora de Development.
    /// </summary>
    private static readonly string[] KnownPlaceholders =
    [
        "troque-esta-chave-em-producao",
        "dev-only-change-this-face-storage-key",
        "dev-only-change-this-data-key",
        "change-me",
    ];

    public static byte[] Resolve(string? configuredValue, string settingName)
    {
        var value = configuredValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{settingName} não configurada.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
            // Não é base64: tratado como frase-senha abaixo.
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    public static byte[] Derive(byte[] masterKey, string purpose)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes(purpose));
    }

    public static bool IsWeak(string? configuredValue)
    {
        var value = configuredValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (KnownPlaceholders.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            if (Convert.FromBase64String(value).Length == 32)
            {
                return false;
            }
        }
        catch (FormatException)
        {
        }

        return value.Length < 32;
    }
}
