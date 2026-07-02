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


    public static double? ExtractCodId(JsonElement result)
    {
        return TryFindCodId(result, out var codId) ? codId : null;
    }

    private static bool TryFindCodId(JsonElement element, out double codId)
    {
        codId = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "codID", StringComparison.OrdinalIgnoreCase)
                    && TryReadDouble(property.Value, out codId))
                {
                    return true;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    && TryFindCodId(property.Value, out codId))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindCodId(item, out codId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value
            ),
            _ => false
        };
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
