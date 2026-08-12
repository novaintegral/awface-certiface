using System.Text.Json;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;

namespace AWFace.Api.Endpoints;

public static class FacetecEndpoints
{
    public static IEndpointRouteBuilder MapFacetecEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/facetec").WithTags("AWFace FaceTec");

        group.MapPost("/v10/3d/process-request", ProcessV10RequestAsync);
        group.MapPost("/3d/process-request", ProcessV10RequestAsync)
            .WithDescription("Alias legado do endpoint FaceTec V10.");

        group.MapPost("/v9/3d/initialize", async (
            JsonElement payload,
            CertifaceClient certiface,
            CancellationToken cancellationToken) =>
        {
            var response = await certiface.InitializeV9Async(payload, cancellationToken);
            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        group.MapPost("/v9/3d/session-token", async (
            JsonElement payload,
            CertifaceClient certiface,
            CancellationToken cancellationToken) =>
        {
            var response = await certiface.CreateV9SessionTokenAsync(payload, cancellationToken);
            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        group.MapPost("/v9/3d/liveness", async (
            JsonElement payload,
            CertifaceClient certiface,
            AwfaceRepository repository,
            TenantWebhookClient webhookClient,
            FaceAssetStorage faceStorage,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AWFace.FaceTec.V9.Liveness");
            var response = await certiface.ProcessV9LivenessAsync(payload, cancellationToken);

            LogProviderFailure(response, payload, "V9 liveness", logger);
            await RegisterSdkResultAndNotifyTenantAsync(
                payload,
                response,
                LivenessEngine.V9,
                repository,
                certiface,
                webhookClient,
                faceStorage,
                logger,
                cancellationToken
            );

            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        return app;
    }

    private static async Task<IResult> ProcessV10RequestAsync(
        JsonElement payload,
        CertifaceClient certiface,
        AwfaceRepository repository,
        TenantWebhookClient webhookClient,
        FaceAssetStorage faceStorage,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AWFace.FaceTec.V10.ProcessRequest");
        var response = await certiface.Process3dRequestAsync(payload, cancellationToken);

        LogProviderFailure(response, payload, "V10 process-request", logger);
        await RegisterSdkResultAndNotifyTenantAsync(
            payload,
            response,
            LivenessEngine.V10,
            repository,
            certiface,
            webhookClient,
            faceStorage,
            logger,
            cancellationToken
        );

        return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
    }

    private static void LogProviderFailure(
        CertifaceProxyResponse response,
        JsonElement payload,
        string operation,
        ILogger logger)
    {
        if (response.StatusCode < 400)
        {
            return;
        }

        logger.LogWarning(
            "Certiface {Operation} retornou {StatusCode}. Campos recebidos pelo AWFace: {PayloadKeys}. Body: {Body}",
            operation,
            response.StatusCode,
            string.Join(",", payload.EnumerateObject().Select(item => item.Name)),
            response.Body
        );
    }

    private static async Task RegisterSdkResultAndNotifyTenantAsync(
        JsonElement requestPayload,
        CertifaceProxyResponse response,
        LivenessEngine engine,
        AwfaceRepository repository,
        CertifaceClient certiface,
        TenantWebhookClient webhookClient,
        FaceAssetStorage faceStorage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != StatusCodes.Status200OK)
        {
            return;
        }

        if (!requestPayload.TryGetProperty("appkey", out var appkeyElement)
            || string.IsNullOrWhiteSpace(appkeyElement.GetString()))
        {
            logger.LogWarning("Resposta FaceTec {Engine} concluida sem appkey no payload AWFace.", engine);
            return;
        }

        var appkey = appkeyElement.GetString()!;
        var journey = await repository.GetJourneyByAppkeyAsync(appkey, cancellationToken);
        if (journey is null)
        {
            logger.LogWarning(
                "Resposta FaceTec {Engine} nao foi associada a uma jornada AWFace. Appkey nao encontrada.",
                engine
            );
            return;
        }

        if (!TryParseProviderResponse(response.Body, out var sdkResult, logger, engine, journey.Id))
        {
            return;
        }

        using (sdkResult)
        {
            var deviceLocationJson = ExtractDeviceLocationJson(requestPayload);
            var codId = TryGetCodId(sdkResult.RootElement, out var parsedCodId) ? parsedCodId : 0d;

            if (codId == 300.1d)
            {
                await repository.RegisterRejectedLivenessAsync(
                    journey.Id,
                    appkey,
                    engine,
                    response.Body,
                    deviceLocationJson,
                    cancellationToken
                );

                await DispatchTenantWebhookAsync(
                    journey,
                    appkey,
                    sdkResult.RootElement,
                    deviceLocationJson,
                    repository,
                    webhookClient,
                    logger
                );

                logger.LogInformation(
                    "Resposta FaceTec {Engine} 300.1 registrada para JourneyId {JourneyId}. A jornada permanecera apta para retentativa.",
                    engine,
                    journey.Id
                );
                return;
            }

            await repository.RegisterLivenessSubmissionAsync(
                journey.Id,
                appkey,
                engine,
                response.Body,
                deviceLocationJson,
                cancellationToken
            );

            if (codId == 200d)
            {
                await CompleteSuccessfulJourneyWithDocumentResultAsync(
                    journey,
                    appkey,
                    engine,
                    sdkResult.RootElement,
                    deviceLocationJson,
                    repository,
                    certiface,
                    webhookClient,
                    faceStorage,
                    logger,
                    cancellationToken
                );
                return;
            }

            var finalStatus = codId == 300.2d ? JourneyStatus.FAILED : JourneyStatus.COMPLETED;
            await repository.MarkJourneyCompletedAsync(
                journey.Id,
                sdkResult,
                deviceLocationJson,
                "completo",
                cancellationToken,
                finalStatus
            );

            await DispatchTenantWebhookAsync(
                journey,
                appkey,
                sdkResult.RootElement,
                deviceLocationJson,
                repository,
                webhookClient,
                logger
            );

            logger.LogInformation(
                "Resposta FaceTec {Engine} codID {CodId} finalizou JourneyId {JourneyId} sem depender de webhook terminal Certiface.",
                engine,
                codId,
                journey.Id
            );
        }
    }

    private static async Task CompleteSuccessfulJourneyWithDocumentResultAsync(
        JourneySession journey,
        string appkey,
        LivenessEngine engine,
        JsonElement sdkResult,
        string? deviceLocationJson,
        AwfaceRepository repository,
        CertifaceClient certiface,
        TenantWebhookClient webhookClient,
        FaceAssetStorage faceStorage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var documentResult = await certiface.GetDocumentResultAsync(appkey, cancellationToken);
            await repository.MarkJourneyCompletedAsync(
                journey.Id,
                documentResult,
                deviceLocationJson,
                "completo",
                cancellationToken,
                JourneyStatus.COMPLETED
            );
            await StoreFrontalFaceIfPresentAsync(
                journey,
                documentResult.RootElement,
                repository,
                faceStorage,
                logger,
                cancellationToken
            );

            await DispatchTenantWebhookAsync(
                journey,
                appkey,
                documentResult.RootElement,
                deviceLocationJson,
                repository,
                webhookClient,
                logger
            );

            logger.LogInformation(
                "Resposta FaceTec {Engine} codID 200 finalizou JourneyId {JourneyId}; document/result consultado e webhook Tenant disparado.",
                engine,
                journey.Id
            );
        }
        catch (Exception exception) when (exception is CertifaceProviderException or JsonException)
        {
            logger.LogWarning(
                exception,
                "Resposta FaceTec {Engine} codID 200 para JourneyId {JourneyId}, mas document/result nao foi obtido. Usando retorno do SDK como contingencia.",
                engine,
                journey.Id
            );

            using var fallbackDocument = JsonDocument.Parse(sdkResult.GetRawText());
            await repository.MarkJourneyCompletedAsync(
                journey.Id,
                fallbackDocument,
                deviceLocationJson,
                "completo",
                CancellationToken.None,
                JourneyStatus.COMPLETED
            );

            await DispatchTenantWebhookAsync(
                journey,
                appkey,
                fallbackDocument.RootElement,
                deviceLocationJson,
                repository,
                webhookClient,
                logger
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

    private static bool TryParseProviderResponse(
        string? responseBody,
        out JsonDocument providerResult,
        ILogger logger,
        LivenessEngine engine,
        Guid journeyId)
    {
        providerResult = null!;
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            logger.LogWarning(
                "Resposta FaceTec {Engine} vazia para JourneyId {JourneyId}; webhook Tenant nao sera disparado.",
                engine,
                journeyId
            );
            return false;
        }

        try
        {
            providerResult = JsonDocument.Parse(responseBody);
            return true;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception,
                "Resposta FaceTec {Engine} invalida para JourneyId {JourneyId}; webhook Tenant nao sera disparado.",
                engine,
                journeyId
            );
            return false;
        }
    }

    private static async Task DispatchTenantWebhookAsync(
        JourneySession journey,
        string appkey,
        JsonElement providerResult,
        string? deviceLocationJson,
        AwfaceRepository repository,
        TenantWebhookClient webhookClient,
        ILogger logger)
    {
        var callbackPayload = TenantCallbackPayloadFactory.Create(
            journey,
            appkey,
            providerResult,
            deviceLocationJson
        );

        var callbackStatus = (int?)null;
        var callbackResponseBody = (string?)null;
        var callbackDelivered = false;

        try
        {
            var callbackResponse = await webhookClient.SendAsync(
                journey.Tenant,
                callbackPayload,
                CancellationToken.None
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
                "Falha ao enviar webhook do Tenant apos retorno FaceTec. JourneyId {JourneyId}.",
                journey.Id
            );
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
            "Webhook do Tenant disparado apos retorno FaceTec para JourneyId {JourneyId}. entregue={Delivered}, status={CallbackStatus}.",
            journey.Id,
            callbackDelivered,
            callbackStatus
        );
    }

    private static bool TryGetCodId(JsonElement response, out double codId)
    {
        codId = 0;
        if (!response.TryGetProperty("codID", out var codIdElement))
        {
            return false;
        }

        return codIdElement.ValueKind switch
        {
            JsonValueKind.Number => codIdElement.TryGetDouble(out codId),
            JsonValueKind.String => double.TryParse(
                codIdElement.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out codId
            ),
            _ => false
        };
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
}