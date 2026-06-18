using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;

namespace AWFace.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhookliveness", async (
            CertifaceWebhookRequest request,
            AwfaceRepository repository,
            CertifaceClient certiface,
            FaceAssetStorage faceStorage,
            ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AWFace.WebhookLiveness");
            var journey = await repository.GetJourneyByAppkeyAsync(request.Appkey, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound(new { message = "Appkey não associada a uma jornada AWFace." });
            }

            using var result = await certiface.GetDocumentResultAsync(request.Appkey, cancellationToken);
            await repository.MarkJourneyCompletedAsync(journey.Id, result, null, cancellationToken);
            await StoreFrontalFaceIfPresentAsync(journey, result.RootElement, repository, faceStorage, logger, cancellationToken);

            var callbackPayload = new
            {
                status = request.Status,
                appkey = request.Appkey,
                journeyId = journey.Id,
                tenantId = journey.Tenant.Id,
                idExternoCliente = journey.Subject.ExternalClientId,
                deviceLocation = (JsonNode?)null,
                result = CreateCallbackResult(result.RootElement)
            };

            using var callbackClient = httpClientFactory.CreateClient("TenantCallback");
            callbackClient.DefaultRequestHeaders.Remove("X-AWFace-SecureCallback");
            callbackClient.DefaultRequestHeaders.Add("X-AWFace-SecureCallback", journey.Tenant.SecureCallbackToken);

            using var response = await callbackClient.PostAsJsonAsync(journey.Tenant.CallbackUrl, callbackPayload, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            await repository.RegisterCallbackDeliveryAsync(
                journey.Id,
                journey.Tenant.CallbackUrl,
                callbackPayload,
                (int)response.StatusCode,
                responseBody,
                cancellationToken
            );

            return Results.Ok(new { received = true, callbackStatus = (int)response.StatusCode });
        }).WithTags("AWFace Webhooks");

        return app;
    }

    private static async Task StoreFrontalFaceIfPresentAsync(
        JourneySession journey,
        JsonElement certifaceResult,
        AwfaceRepository repository,
        FaceAssetStorage faceStorage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var frontalFaceBase64 = CertifaceResultParser.ExtractFrontalFaceBase64(certifaceResult);
        if (string.IsNullOrWhiteSpace(frontalFaceBase64))
        {
            logger.LogWarning("Resultado Certiface da jornada {JourneyId} não contém fotos.facecaptcha.frontal.", journey.Id);
            return;
        }

        try
        {
            var asset = await faceStorage.SaveFrontalFaceAsync(journey.Tenant.Id, journey.Id, frontalFaceBase64, cancellationToken);
            await repository.UpsertJourneyFaceAssetAsync(journey.Id, journey.Tenant.Id, "FRONTAL", asset, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao armazenar face frontal da jornada {JourneyId}.", journey.Id);
        }
    }

    private static JsonNode? CreateCallbackResult(JsonElement result)
    {
        var resultNode = JsonNode.Parse(result.GetRawText());
        if (resultNode is JsonObject resultObject)
        {
            resultObject.Remove("responseBlob");
        }

        return resultNode;
    }
}
