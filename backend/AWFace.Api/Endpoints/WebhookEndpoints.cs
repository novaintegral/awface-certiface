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
        app.MapPost("/api/awface/webhooks/certiface", HandleCertifaceWebhookAsync)
            .WithTags("AWFace Webhooks")
            .WithDescription("Recebe a notificacao do provedor Certiface, registra o status recebido e finaliza a jornada AWFace.");

        app.MapPost("/webhookliveness", HandleCertifaceWebhookAsync)
            .WithTags("AWFace Webhooks")
            .WithDescription("Alias legado do webhook da Certiface.");

        return app;
    }

    private static async Task<IResult> HandleCertifaceWebhookAsync(
        CertifaceWebhookRequest request,
        AwfaceRepository repository,
        CertifaceClient certiface,
        FaceAssetStorage faceStorage,
        TenantWebhookClient webhookClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AWFace.Certiface.Webhook");
        var providerStatus = string.IsNullOrWhiteSpace(request.Status) ? "Nao informado" : request.Status.Trim();
        var appkey = request.Appkey?.Trim();

        if (string.IsNullOrWhiteSpace(appkey))
        {
            return Results.BadRequest(new { received = false, message = "Appkey nao informada." });
        }

        var journey = await repository.GetJourneyByAppkeyAsync(appkey, cancellationToken);
        if (journey is null)
        {
            logger.LogWarning("Webhook Certiface recebido para appkey nao associada ao AWFace.");
            return Results.NotFound(new { received = false, message = "Appkey nao associada a uma jornada AWFace." });
        }

        if (!string.Equals(journey.Appkey, appkey, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Webhook Certiface ignorado para appkey supersedida. JourneyId {JourneyId}, Status={Status}.",
                journey.Id,
                providerStatus
            );
            return Results.Accepted(value: new
            {
                received = true,
                superseded = true,
                message = "A appkey notificada nao e mais a appkey ativa da jornada."
            });
        }

        var notificationId = await repository.TryBeginProviderNotificationAsync(
            journey.Id,
            appkey,
            providerStatus,
            cancellationToken
        );
        if (notificationId is null)
        {
            logger.LogInformation(
                "Webhook Certiface duplicado ou ja em processamento para JourneyId {JourneyId}, status {Status}.",
                journey.Id,
                providerStatus
            );
            return Results.Ok(new { received = true, duplicate = true });
        }

        try
        {
            var submission = await repository.GetLivenessSubmissionAsync(appkey, cancellationToken);
            if (TryParseSubmissionResult(submission, out var immediateResult)
                && CertifaceResultParser.ExtractCodId(immediateResult.RootElement) == 300.1d)
            {
                await repository.ReactivateJourneyAppkeyAsync(journey.Id, cancellationToken);

                var retryCallbackPayload = TenantCallbackPayloadFactory.Create(
                    journey,
                    appkey,
                    immediateResult.RootElement,
                    submission?.DeviceLocationJson
                );
                var retryCallbackStatus = (int?)null;
                var retryCallbackResponseBody = (string?)null;
                var retryCallbackDelivered = false;

                try
                {
                    var callbackResponse = await webhookClient.SendAsync(
                        journey.Tenant,
                        retryCallbackPayload,
                        cancellationToken
                    );
                    retryCallbackStatus = callbackResponse.StatusCode;
                    retryCallbackResponseBody = callbackResponse.ResponseBody;
                    retryCallbackDelivered = callbackResponse.Delivered;
                }
                catch (Exception exception)
                {
                    retryCallbackResponseBody = exception.Message;
                    logger.LogError(
                        exception,
                        "Falha ao enviar webhook do Tenant para prova de vida invalida. JourneyId {JourneyId}.",
                        journey.Id
                    );
                }

                await repository.RegisterCallbackDeliveryAsync(
                    journey.Id,
                    journey.Tenant.CallbackUrl,
                    retryCallbackPayload,
                    retryCallbackStatus,
                    retryCallbackResponseBody,
                    cancellationToken
                );
                await repository.CompleteProviderNotificationAsync(notificationId.Value, cancellationToken);
                var immediateCodId = CertifaceResultParser.ExtractCodId(immediateResult.RootElement) ?? 300.1d;

                logger.LogInformation(
                    "Webhook Certiface recebido como prova de vida invalida com retentativa permitida. JourneyId {JourneyId}, StatusProvider={ProviderStatus}, CodID={CodId}, callbackTenantEntregue={Delivered}, callbackStatus={CallbackStatus}.",
                    journey.Id,
                    providerStatus,
                    immediateCodId,
                    retryCallbackDelivered,
                    retryCallbackStatus
                );

                immediateResult.Dispose();

                return Results.Ok(new
                {
                    received = true,
                    retryAllowed = true,
                    codID = immediateCodId,
                    callbackDelivered = retryCallbackDelivered,
                    callbackStatus = retryCallbackStatus,
                    providerStatus
                });
            }

            using var result = await certiface.GetDocumentResultAsync(appkey, cancellationToken);
            var codId = CertifaceResultParser.ExtractCodId(result.RootElement) ?? 0d;
            var finalStatus = ResolveFinalJourneyStatus(codId);
            await repository.MarkJourneyCompletedAsync(
                journey.Id,
                result,
                submission?.DeviceLocationJson,
                providerStatus,
                cancellationToken,
                finalStatus
            );
            await StoreFrontalFaceIfPresentAsync(
                journey,
                result.RootElement,
                repository,
                faceStorage,
                logger,
                cancellationToken
            );

            if (codId is >= 200d and < 300d)
            {
                await repository.CompleteProviderNotificationAsync(notificationId.Value, cancellationToken);

                logger.LogInformation(
                    "Webhook Certiface recebido e jornada finalizada para JourneyId {JourneyId}. StatusProvider={ProviderStatus}. Aguardando polling de completion para notificar o Tenant.",
                    journey.Id,
                    providerStatus
                );

                return Results.Ok(new
                {
                    received = true,
                    tenantCallbackPending = true,
                    providerStatus
                });
            }

            var callbackPayload = TenantCallbackPayloadFactory.Create(
                journey,
                appkey,
                result.RootElement,
                submission?.DeviceLocationJson
            );

            var callbackStatus = (int?)null;
            var callbackResponseBody = (string?)null;
            var callbackDelivered = false;

            try
            {
                var callbackResponse = await webhookClient.SendAsync(
                    journey.Tenant,
                    callbackPayload,
                    cancellationToken
                );
                callbackStatus = callbackResponse.StatusCode;
                callbackResponseBody = callbackResponse.ResponseBody;
                callbackDelivered = callbackResponse.Delivered;
            }
            catch (Exception exception)
            {
                callbackResponseBody = exception.Message;
                logger.LogError(
                    exception,
                    "Falha ao enviar webhook do Tenant para JourneyId {JourneyId}.",
                    journey.Id
                );
            }

            await repository.RegisterCallbackDeliveryAsync(
                journey.Id,
                journey.Tenant.CallbackUrl,
                callbackPayload,
                callbackStatus,
                callbackResponseBody,
                cancellationToken
            );
            await repository.CompleteProviderNotificationAsync(notificationId.Value, cancellationToken);

            logger.LogInformation(
                "Webhook Certiface recebido e jornada finalizada para JourneyId {JourneyId}. StatusProvider={ProviderStatus}, callbackTenantEntregue={Delivered}, callbackStatus={CallbackStatus}.",
                journey.Id,
                providerStatus,
                callbackDelivered,
                callbackStatus
            );

            return Results.Ok(new
            {
                received = true,
                callbackDelivered,
                callbackStatus,
                providerStatus
            });
        }
        catch (Exception exception)
        {
            await repository.FailProviderNotificationAsync(
                notificationId.Value,
                exception.Message,
                CancellationToken.None
            );
            logger.LogError(
                exception,
                "Falha ao processar webhook Certiface para JourneyId {JourneyId}.",
                journey.Id
            );

            return Results.Json(
                new { received = true, message = exception.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
    }

    private static bool TryParseSubmissionResult(LivenessSubmission? submission, out JsonDocument result)
    {
        result = null!;
        if (submission is null || string.IsNullOrWhiteSpace(submission.ProviderResponseJson))
        {
            return false;
        }

        try
        {
            result = JsonDocument.Parse(submission.ProviderResponseJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private static JourneyStatus ResolveFinalJourneyStatus(double codId)
    {
        return codId is >= 200d and < 300d
            ? JourneyStatus.COMPLETED
            : JourneyStatus.FAILED;
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
            logger.LogWarning(
                "Resultado Certiface da jornada {JourneyId} nao contem fotos.facecaptcha.frontal.",
                journey.Id
            );
            return;
        }

        try
        {
            var asset = await faceStorage.SaveFrontalFaceAsync(
                journey.Tenant.Id,
                journey.Id,
                frontalFaceBase64,
                cancellationToken
            );
            await repository.UpsertJourneyFaceAssetAsync(
                journey.Id,
                journey.Tenant.Id,
                "FRONTAL",
                asset,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Falha ao armazenar face frontal da jornada {JourneyId}.",
                journey.Id
            );
        }
    }

    private static JsonNode? ParseJsonNode(string? json)
    {
        return string.IsNullOrWhiteSpace(json) || json == "null" ? null : JsonNode.Parse(json);
    }

    private static JsonNode? CreateCallbackResult(JsonElement result)
    {
        var resultNode = JsonNode.Parse(result.GetRawText());
        if (resultNode is JsonObject resultObject)
        {
            resultObject.Remove("responseBlob");
            resultObject.Remove("appkey");
            resultObject.Remove("Appkey");
        }

        return resultNode;
    }
}
