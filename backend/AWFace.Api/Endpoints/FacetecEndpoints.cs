using System.Text.Json;
using System.Text.Json.Nodes;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;

namespace AWFace.Api.Endpoints;

public static class FacetecEndpoints
{
    public static IEndpointRouteBuilder MapFacetecEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/facetec").WithTags("AWFace FaceTec");

        group.MapPost("/3d/process-request", async (
            JsonElement payload,
            CertifaceClient certiface,
            IServiceScopeFactory scopeFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AWFace.FaceTec.ProcessRequest");
            var response = await certiface.Process3dRequestAsync(payload, cancellationToken);

            if (response.StatusCode >= 400)
            {
                logger.LogWarning(
                    "Certiface process-request returned {StatusCode}. Payload keys: {PayloadKeys}. Body: {Body}",
                    response.StatusCode,
                    string.Join(",", payload.EnumerateObject().Select(item => item.Name)),
                    response.Body
                );
            }

            QueueCompletionCallbackIfNeeded(payload, response, scopeFactory, loggerFactory);

            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        return app;
    }

    private static void QueueCompletionCallbackIfNeeded(
        JsonElement requestPayload,
        CertifaceProxyResponse response,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AWFace.FaceTec.Callback");

        if (response.StatusCode != StatusCodes.Status200OK || string.IsNullOrWhiteSpace(response.Body))
        {
            return;
        }

        using var responseDocument = JsonDocument.Parse(response.Body);
        var responseRoot = responseDocument.RootElement;

        if (!responseRoot.TryGetProperty("valid", out var validElement) || !validElement.GetBoolean())
        {
            return;
        }

        if (!requestPayload.TryGetProperty("appkey", out var appkeyElement))
        {
            logger.LogWarning("Process-request retornou valid=true, mas o payload de entrada não contém appkey.");
            return;
        }

        var appkey = appkeyElement.GetString();
        if (string.IsNullOrWhiteSpace(appkey))
        {
            logger.LogWarning("Process-request retornou valid=true, mas a appkey de entrada está vazia.");
            return;
        }

        var responseBody = response.Body;
        var deviceLocationJson = ExtractDeviceLocationJson(requestPayload);
        _ = Task.Run(() => FinalizeJourneyAndSendCallbackAsync(appkey, responseBody, deviceLocationJson, scopeFactory, loggerFactory));
    }

    private static async Task FinalizeJourneyAndSendCallbackAsync(
        string appkey,
        string processRequestBody,
        string? deviceLocationJson,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AWFace.FaceTec.Callback");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<AwfaceRepository>();
            var certiface = scope.ServiceProvider.GetRequiredService<CertifaceClient>();
            var faceStorage = scope.ServiceProvider.GetRequiredService<FaceAssetStorage>();
            var webhookClient = scope.ServiceProvider.GetRequiredService<TenantWebhookClient>();

            var journey = await repository.GetJourneyByAppkeyAsync(appkey, CancellationToken.None);
            if (journey is null)
            {
                logger.LogWarning("Process-request válido não foi associado a nenhuma jornada AWFace. Appkey não encontrada.");
                return;
            }

            if (journey.Status == JourneyStatus.COMPLETED)
            {
                logger.LogInformation("Jornada {JourneyId} já estava concluída. Callback não reenviado.", journey.Id);
                return;
            }

            using var resultDocument = JsonDocument.Parse(processRequestBody);
            var result = resultDocument.RootElement.Clone();
            var callbackResult = CreateCallbackResult(result);
            var deviceLocation = string.IsNullOrWhiteSpace(deviceLocationJson) ? null : JsonNode.Parse(deviceLocationJson);

            using var certifaceResultDocument = await certiface.GetDocumentResultAsync(appkey, CancellationToken.None);
            await repository.MarkJourneyCompletedAsync(journey.Id, certifaceResultDocument, deviceLocationJson, CancellationToken.None);
            await StoreFrontalFaceIfPresentAsync(journey, certifaceResultDocument.RootElement, repository, faceStorage, logger, CancellationToken.None);

            var callbackPayload = new
            {
                status = "Completo",
                appkey,
                journeyId = journey.Id,
                tenantId = journey.Tenant.Id,
                idExternoCliente = journey.Subject.ExternalClientId,
                deviceLocation,
                result = callbackResult
            };
            var callbackPayloadJson = JsonSerializer.Serialize(callbackPayload);

            // // Simulação temporária enquanto a aplicação host ainda não responde ao UrlCallback.
            // // Mantém o fluxo assíncrono e o registro em banco, mas força sucesso HTTP 200.
            // var callbackDelivered = true;
            // int? callbackStatus = StatusCodes.Status200OK;
            // string? callbackResponseBody = "UrlCallback simulado com sucesso.";

            // logger.LogInformation(
            //     "UrlCallback simulado para JourneyId {JourneyId}. POST real desativado. TargetUrl={TargetUrl}. Payload={Payload}.",
            //     journey.Id,
            //     journey.Tenant.CallbackUrl,
            //     callbackPayloadJson
            // );

            
            // Fluxo real do UrlCallback. Reative este bloco quando a aplicação host estiver pronta
            // e comente/remova a simulação temporária acima.
            var callbackDelivered = false;
            int? callbackStatus = null;
            string? callbackResponseBody = null;

            try
            {
                var callbackResponse = await webhookClient.SendAsync(journey.Tenant, callbackPayload, CancellationToken.None);
                callbackStatus = callbackResponse.StatusCode;
                callbackResponseBody = callbackResponse.ResponseBody;
                callbackDelivered = callbackResponse.Delivered;
            }
            catch (Exception exception)
            {
                callbackResponseBody = exception.Message;
                logger.LogError(exception, "Falha ao enviar UrlCallback para JourneyId {JourneyId}.", journey.Id);
            }
            

            await repository.RegisterCallbackDeliveryAsync(
                journey.Id,
                journey.Tenant.CallbackUrl,
                callbackPayload,
                callbackStatus,
                callbackResponseBody,
                CancellationToken.None
            );

            logger.LogInformation(
                "Jornada {JourneyId} concluída. Callback entregue={CallbackDelivered}, status={CallbackStatus}.",
                journey.Id,
                callbackDelivered,
                callbackStatus
            );
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha inesperada ao finalizar jornada e enviar UrlCallback.");
        }
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

    private static string? ExtractDeviceLocationJson(JsonElement requestPayload)
    {
        if (!requestPayload.TryGetProperty("deviceLocation", out var deviceLocation)
            || deviceLocation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return deviceLocation.GetRawText();
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
