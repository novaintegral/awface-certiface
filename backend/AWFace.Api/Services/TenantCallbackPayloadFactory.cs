using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AWFace.Api.Domain;

namespace AWFace.Api.Services;

public static class TenantCallbackPayloadFactory
{
    private const string CompletedStatus = "completo";

    public static JsonObject Create(
        JourneySession journey,
        string appkey,
        JsonElement providerResult,
        string? deviceLocationJson)
    {
        var codId = CertifaceResultParser.ExtractCodId(providerResult) ?? ExtractFlatCodId(providerResult) ?? 0d;
        var result = new JsonObject
        {
            ["status"] = CompletedStatus,
            ["idExternoCliente"] = ExtractString(providerResult, "idExternoCliente") ?? journey.Subject.ExternalClientId
        };

        if (IsSuccessful(codId) && TryCloneProperty(providerResult, "certifaceID", out var certifaceId))
        {
            result["certifaceID"] = certifaceId;
        }

        var createdAt = ExtractString(providerResult, "dataCriacaoAppkey");
        if (!string.IsNullOrWhiteSpace(createdAt))
        {
            result["dataCriacaoAppkey"] = createdAt;
        }

        result["facecaptcha"] = CreateFacecaptchaNode(providerResult, codId);

        var payload = new JsonObject
        {
            ["status"] = CompletedStatus,
            ["appkey"] = appkey,
            ["journeyId"] = journey.Id.ToString(),
            ["tenantId"] = journey.Tenant.Id.ToString(),
            ["idExternoCliente"] = journey.Subject.ExternalClientId,
            ["deviceLocation"] = ParseJsonNode(deviceLocationJson),
            ["result"] = result
        };

        return payload;
    }

    private static JsonObject CreateFacecaptchaNode(JsonElement providerResult, double codId)
    {
        if (providerResult.TryGetProperty("facecaptcha", out var facecaptcha)
            && facecaptcha.ValueKind == JsonValueKind.Object)
        {
            return new JsonObject
            {
                ["causa"] = ExtractString(facecaptcha, "causa") ?? ExtractString(providerResult, "cause"),
                ["codID"] = ExtractNumber(facecaptcha, "codID") ?? codId,
                ["protocolo"] = ExtractFlexibleValue(facecaptcha, "protocolo") ?? ExtractFlexibleValue(providerResult, "protocol"),
                ["validado"] = ExtractBool(facecaptcha, "validado") ?? ExtractBool(providerResult, "valid") ?? IsSuccessful(codId)
            };
        }

        return new JsonObject
        {
            ["causa"] = ExtractString(providerResult, "cause") ?? ExtractString(providerResult, "causa"),
            ["codID"] = codId,
            ["protocolo"] = ExtractFlexibleValue(providerResult, "protocol") ?? ExtractFlexibleValue(providerResult, "protocolo"),
            ["validado"] = ExtractBool(providerResult, "valid") ?? ExtractBool(providerResult, "validado") ?? IsSuccessful(codId)
        };
    }

    private static bool IsSuccessful(double codId) => codId is >= 200d and < 300d;

    private static JsonNode? ParseJsonNode(string? json)
    {
        return string.IsNullOrWhiteSpace(json) || json == "null" ? null : JsonNode.Parse(json);
    }

    private static bool TryCloneProperty(JsonElement element, string propertyName, out JsonNode? node)
    {
        node = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        node = JsonNode.Parse(property.GetRawText());
        return node is not null;
    }

    private static double? ExtractFlatCodId(JsonElement element)
    {
        return ExtractNumber(element, "codID") ?? ExtractNumber(element, "codId");
    }

    private static string? ExtractString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static double? ExtractNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static bool? ExtractBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static JsonNode? ExtractFlexibleValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(property.GetString()),
            JsonValueKind.Number when property.TryGetInt64(out var longValue) => JsonValue.Create(longValue),
            JsonValueKind.Number when property.TryGetDouble(out var doubleValue) => JsonValue.Create(doubleValue),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            _ => JsonNode.Parse(property.GetRawText())
        };
    }
}