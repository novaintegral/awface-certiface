using System.Text.Json;

namespace AWFace.Api.Services;

public static class CertifaceResultParser
{
    public static string? ExtractStatus(JsonElement result)
    {
        if (!result.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = status.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool IsTerminalStatus(string? status)
    {
        return string.Equals(status, "Completo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Erro", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractFrontalFaceBase64(JsonElement result)
    {
        if (!result.TryGetProperty("fotos", out var fotos)
            || !fotos.TryGetProperty("facecaptcha", out var facecaptcha)
            || !facecaptcha.TryGetProperty("frontal", out var frontal)
            || frontal.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = frontal.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
