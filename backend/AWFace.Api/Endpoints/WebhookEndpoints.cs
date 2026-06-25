using System.Text.Json;
using System.Text.Json.Nodes;
using AWFace.Api.Configuration;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/awface/webhooks/certiface", HandleCertifaceWebhookAsync)
            .WithTags("AWFace Webhooks")
            .WithDescription("Recebe a notificação terminal da Certiface e finaliza a jornada AWFace.");

        app.MapPost("/webhookliveness", HandleCertifaceWebhookAsync)
            .WithTags("AWFace Webhooks")
            .WithDescription("Alias legado do webhook terminal da Certiface.");

        return app;
    }

    private static async Task<IResult> HandleCertifaceWebhookAsync(
        CertifaceWebhookRequest request,
        AwfaceRepository repository,
        CertifaceClient certiface,
        FaceAssetStorage faceStorage,
        TenantWebhookClient webhookClient,
        IOptions<AwfaceOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AWFace.Certiface.Webhook");
        var providerStatus = request.Status?.Trim();
        var appkey = request.Appkey?.Trim();

        if (!CertifaceResultParser.IsTerminalStatus(providerStatus))
        {
            logger.LogWarning(
                "Webhook Certiface ignorado porque o status não é terminal. Status={Status}.",
                providerStatus ?? "não informado"
            );
            return Results.Accepted(value: new
            {
                received = true,
                processed = false,
                message = "Somente os status Completo ou Erro finalizam a jornada."
            });
        }

        if (string.IsNullOrWhiteSpace(appkey))
        {
            return Results.BadRequest(new { message = "Appkey não informada." });
        }

        var journey = await repository.GetJourneyByAppkeyAsync(appkey, cancellationToken);
        if (journey is null)
        {
            logger.LogWarning("Webhook Certiface recebido para appkey não associada ao AWFace.");
            return Results.NotFound(new { message = "Appkey não associada a uma jornada AWFace." });
        }

        var notificationId = await repository.TryBeginProviderNotificationAsync(
            journey.Id,
            appkey,
            providerStatus!,
            cancellationToken
        );
        if (notificationId is null)
        {
            logger.LogInformation(
                "Webhook Certiface duplicado ou já em processamento para JourneyId {JourneyId}, status {Status}.",
                journey.Id,
                providerStatus
            );
            return Results.Ok(new { received = true, processed = false, duplicate = true });
        }

        try
        {
            var submission = await repository.GetLivenessSubmissionAsync(appkey, cancellationToken);
            if (submission is null)
            {
                throw new InvalidOperationException(
                    $"A jornada {journey.Id} não possui uma submissão de liveness registrada."
                );
            }

            using var result = await certiface.GetDocumentResultAsync(appkey, cancellationToken);
            var resultStatus = CertifaceResultParser.ExtractStatus(result.RootElement);
            var homologationOverride = ShouldAcceptHomologationResult(
                providerStatus!,
                resultStatus,
                options.Value
            );
            if (!CertifaceResultParser.IsTerminalStatus(resultStatus) && !homologationOverride)
            {
                throw new InvalidOperationException(
                    $"A Certiface notificou status '{providerStatus}', mas document/result retornou '{resultStatus ?? "não informado"}'."
                );
            }

            if (homologationOverride)
            {
                logger.LogWarning(
                    "MODO DE HOMOLOGAÇÃO ATIVO: JourneyId {JourneyId} recebeu webhook Certiface com status Completo, "
                    + "mas document/result retornou Não processado. O fluxo continuará usando o status terminal notificado.",
                    journey.Id
                );
            }

            await repository.MarkJourneyCompletedAsync(
                journey.Id,
                result,
                submission.DeviceLocationJson,
                providerStatus!,
                cancellationToken
            );
            await StoreFrontalFaceIfPresentAsync(
                journey,
                result.RootElement,
                repository,
                faceStorage,
                logger,
                cancellationToken
            );

            using var immediateResultDocument = JsonDocument.Parse(submission.ProviderResponseJson);
            var callbackPayload = new
            {
                status = providerStatus,
                appkey,
                journeyId = journey.Id,
                tenantId = journey.Tenant.Id,
                idExternoCliente = journey.Subject.ExternalClientId,
                deviceLocation = ParseJsonNode(submission.DeviceLocationJson),
                result = CreateCallbackResult(immediateResultDocument.RootElement)
            };

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
                "Webhook Certiface processado para JourneyId {JourneyId}. "
                + "StatusProvider={ProviderStatus}, callbackTenantEntregue={Delivered}, callbackStatus={CallbackStatus}.",
                journey.Id,
                providerStatus,
                callbackDelivered,
                callbackStatus
            );

            return Results.Ok(new
            {
                received = true,
                processed = true,
                callbackDelivered,
                callbackStatus
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
                new { received = true, processed = false, message = exception.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
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
            logger.LogWarning(
                "Resultado Certiface da jornada {JourneyId} não contém fotos.facecaptcha.frontal.",
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

    private static bool ShouldAcceptHomologationResult(
        string providerStatus,
        string? resultStatus,
        AwfaceOptions options)
    {
        return options.Homologation.AcceptNonProcessedResultAfterCompleteNotification
            && string.Equals(providerStatus, "Completo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(resultStatus, "Não processado", StringComparison.OrdinalIgnoreCase);
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
