using System.Security.Cryptography;
using System.Text;

namespace AWFace.Api.Security;

public sealed class SensitiveDataProtector
{
    public string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(value)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string Protect(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public string Unprotect(string value)
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
