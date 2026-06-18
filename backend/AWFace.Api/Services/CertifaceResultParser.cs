using System.Text.Json;

namespace AWFace.Api.Services;

public static class CertifaceResultParser
{
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
