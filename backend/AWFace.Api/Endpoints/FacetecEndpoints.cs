using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Security;
using AWFace.Api.Services;

namespace AWFace.Api.Endpoints;

public static class FacetecEndpoints
{
    /// <summary>
    /// Tempo máximo de processamento de uma submissão do SDK. Não depende da conexão do navegador:
    /// se o usuário fechar a aba, o resultado do provedor ainda é persistido e entregue ao Tenant.
    /// </summary>
    private static readonly TimeSpan SubmissionProcessingTimeout = TimeSpan.FromSeconds(110);
    private static readonly TimeSpan DuplicateWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DuplicatePollInterval = TimeSpan.FromMilliseconds(500);
    private const int MaxLoggedBodyLength = 500;

    public static IEndpointRouteBuilder MapFacetecEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/facetec").WithTags("AWFace FaceTec");

        group.MapPost("/v10/3d/process-request", ProcessV10RequestAsync);
        group.MapPost("/3d/process-request", ProcessV10RequestAsync)
            .WithDescription("Alias legado do endpoint FaceTec V10.");

        group.MapPost("/v9/3d/initialize", async (
            JsonElement payload,
            HttpContext httpContext,
            CertifaceClient certiface,
            AwfaceRepository repository,
            SignedTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAppkeyAsync(payload, httpContext, repository, tokens, cancellationToken);
            if (access.Result is not null)
            {
                return access.Result;
            }

            var response = await certiface.InitializeV9Async(payload, cancellationToken);
            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        group.MapPost("/v9/3d/session-token", async (
            JsonElement payload,
            HttpContext httpContext,
            CertifaceClient certiface,
            AwfaceRepository repository,
            SignedTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var access = await AuthorizeAppkeyAsync(payload, httpContext, repository, tokens, cancellationToken);
            if (access.Result is not null)
            {
                return access.Result;
            }

            var response = await certiface.CreateV9SessionTokenAsync(payload, cancellationToken);
            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        });

        group.MapPost("/v9/3d/liveness", (
            JsonElement payload,
            HttpContext httpContext,
            SubmissionDependencies dependencies,
            CancellationToken cancellationToken) =>
        {
            var requestMaterial = string.Join(
                "|",
                CertifaceClient.GetRequiredString(payload, "sessionId"),
                Sha256Hex(CertifaceClient.GetRequiredString(payload, "faceScan"))
            );

            return ProcessSubmissionAsync(
                payload,
                httpContext,
                LivenessEngine.V9,
                "v9-liveness",
                requestMaterial,
                token => dependencies.Certiface.ProcessV9LivenessAsync(payload, token),
                dependencies,
                cancellationToken
            );
        });

        return app;
    }

    private static Task<IResult> ProcessV10RequestAsync(
        JsonElement payload,
        HttpContext httpContext,
        SubmissionDependencies dependencies,
        CancellationToken cancellationToken)
    {
        return ProcessSubmissionAsync(
            payload,
            httpContext,
            LivenessEngine.V10,
            "v10-process-request",
            CertifaceClient.GetRequiredString(payload, "requestBlob"),
            token => dependencies.Certiface.Process3dRequestAsync(payload, token),
            dependencies,
            cancellationToken
        );
    }

    /// <summary>
    /// Processa uma submissão do SDK de forma idempotente: a mesma requisição (appkey + conteúdo)
    /// é enviada ao provedor uma única vez e as repetições recebem a resposta armazenada.
    /// </summary>
    private static async Task<IResult> ProcessSubmissionAsync(
        JsonElement payload,
        HttpContext httpContext,
        LivenessEngine engine,
        string operation,
        string requestMaterial,
        Func<CancellationToken, Task<CertifaceProxyResponse>> callProvider,
        SubmissionDependencies dependencies,
        CancellationToken requestAborted)
    {
        var repository = dependencies.Repository;
        var access = await AuthorizeAppkeyAsync(payload, httpContext, repository, dependencies.Tokens, requestAborted);
        if (access.Result is not null)
        {
            return access.Result;
        }

        var journey = access.Journey!;
        var appkey = access.Appkey!;
        var logger = dependencies.LoggerFactory.CreateLogger($"AWFace.FaceTec.{engine}");
        var requestKey = Sha256Hex($"{operation}|{appkey}|{requestMaterial}");

        var existing = await repository.TryBeginSdkRequestAsync(requestKey, journey.Id, appkey, operation, requestAborted);
        if (existing is not null)
        {
            logger.LogInformation(
                "Requisição {Operation} repetida para JourneyId {JourneyId}; o provedor não será chamado novamente.",
                operation,
                journey.Id
            );
            return await WaitForDuplicateAsync(repository, requestKey, existing, requestAborted);
        }

        using var processingCts = new CancellationTokenSource(SubmissionProcessingTimeout);
        var processingToken = processingCts.Token;
        CertifaceProxyResponse response;
        try
        {
            response = await callProvider(processingToken);
        }
        catch
        {
            await repository.ReleaseSdkRequestAsync(requestKey, CancellationToken.None);
            throw;
        }

        LogProviderFailure(response, payload, operation, logger);
        if (response.StatusCode != StatusCodes.Status200OK)
        {
            await repository.ReleaseSdkRequestAsync(requestKey, CancellationToken.None);
            return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
        }

        try
        {
            await RegisterSdkResultAsync(payload, response, engine, journey, appkey, requestKey, dependencies, logger, processingToken);
        }
        catch (Exception exception)
        {
            // A resposta do provedor é devolvida ao SDK mesmo assim: blobs FaceTec não podem ser reenviados.
            logger.LogError(
                exception,
                "Falha ao registrar o retorno {Operation} da JourneyId {JourneyId}. Verifique a jornada manualmente.",
                operation,
                journey.Id
            );
        }

        await repository.CompleteSdkRequestAsync(requestKey, response.StatusCode, response.ContentType, response.Body, CancellationToken.None);
        return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
    }

    private static async Task<IResult> WaitForDuplicateAsync(
        AwfaceRepository repository,
        string requestKey,
        SdkRequestRecord record,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(DuplicateWaitTimeout);
        var current = record;
        while (current is not null && !current.Completed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(DuplicatePollInterval, cancellationToken);
            current = await repository.GetSdkRequestAsync(requestKey, cancellationToken);
        }

        if (current is { Completed: true, ResponseBody: not null })
        {
            return Results.Content(
                current.ResponseBody,
                current.ResponseContentType ?? "application/json",
                statusCode: current.ResponseStatus ?? StatusCodes.Status200OK
            );
        }

        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Requisição em processamento",
            detail: "Esta etapa da prova de vida já está sendo processada. Aguarde e tente novamente."
        );
    }

    private static async Task RegisterSdkResultAsync(
        JsonElement requestPayload,
        CertifaceProxyResponse response,
        LivenessEngine engine,
        JourneySession journey,
        string appkey,
        string requestKey,
        SubmissionDependencies dependencies,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var repository = dependencies.Repository;
        if (!TryParseProviderResponse(response.Body, out var sdkResult))
        {
            logger.LogWarning("Resposta FaceTec {Engine} vazia ou inválida para JourneyId {JourneyId}.", engine, journey.Id);
            return;
        }

        using (sdkResult)
        {
            if (!TryGetCodId(sdkResult.RootElement, out var codId))
            {
                // Etapas intermediárias do SDK (ex.: inicialização da sessão V10) não trazem codID.
                return;
            }

            var deviceLocationJson = ExtractDeviceLocationJson(requestPayload);

            if (codId == 300.1d)
            {
                await repository.RegisterRejectedLivenessAsync(journey.Id, appkey, engine, response.Body, deviceLocationJson, cancellationToken);
                await EnqueueTenantWebhookAsync(journey, appkey, requestKey, sdkResult.RootElement, deviceLocationJson, dependencies, cancellationToken);
                logger.LogInformation(
                    "Resposta FaceTec {Engine} 300.1 registrada para JourneyId {JourneyId}. A jornada permanece apta para nova tentativa.",
                    engine,
                    journey.Id
                );
                return;
            }

            if (codId == 200d)
            {
                await repository.RegisterLivenessSubmissionAsync(journey.Id, appkey, engine, response.Body, deviceLocationJson, cancellationToken);
                await CompleteSuccessfulJourneyAsync(journey, appkey, requestKey, engine, sdkResult.RootElement, deviceLocationJson, dependencies, logger, cancellationToken);
                return;
            }

            if (codId == 300.2d)
            {
                await repository.RegisterLivenessSubmissionAsync(journey.Id, appkey, engine, response.Body, deviceLocationJson, cancellationToken);
                await repository.MarkJourneyCompletedAsync(journey.Id, sdkResult, deviceLocationJson, "completo", cancellationToken, JourneyStatus.FAILED);
                await EnqueueTenantWebhookAsync(journey, appkey, requestKey, sdkResult.RootElement, deviceLocationJson, dependencies, cancellationToken);
                logger.LogInformation("Resposta FaceTec {Engine} 300.2 finalizou a JourneyId {JourneyId} como bloqueada.", engine, journey.Id);
                return;
            }

            // codID desconhecido: registrado para análise, sem alterar o estado da jornada nem notificar o Tenant.
            await repository.RegisterAttemptEventAsync(
                journey.Id,
                appkey,
                engine,
                "SDK_UNKNOWN_CODID",
                response.Body,
                deviceLocationJson,
                cancellationToken
            );
            logger.LogWarning(
                "Resposta FaceTec {Engine} com codID não reconhecido ({CodId}) para JourneyId {JourneyId}. A jornada não foi finalizada.",
                engine,
                codId,
                journey.Id
            );
        }
    }

    private static async Task CompleteSuccessfulJourneyAsync(
        JourneySession journey,
        string appkey,
        string requestKey,
        LivenessEngine engine,
        JsonElement sdkResult,
        string? deviceLocationJson,
        SubmissionDependencies dependencies,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var repository = dependencies.Repository;
        try
        {
            using var documentResult = await dependencies.Certiface.GetDocumentResultAsync(appkey, cancellationToken);
            await repository.MarkJourneyCompletedAsync(journey.Id, documentResult, deviceLocationJson, "completo", cancellationToken, JourneyStatus.COMPLETED);
            await StoreFrontalFaceIfPresentAsync(journey, documentResult.RootElement, dependencies, logger, cancellationToken);
            await EnqueueTenantWebhookAsync(journey, appkey, requestKey, documentResult.RootElement, deviceLocationJson, dependencies, cancellationToken);
            logger.LogInformation(
                "Resposta FaceTec {Engine} codID 200 finalizou a JourneyId {JourneyId}; document/result consultado e webhook enfileirado.",
                engine,
                journey.Id
            );
        }
        catch (Exception exception) when (exception is CertifaceProviderException or JsonException)
        {
            logger.LogWarning(
                exception,
                "Resposta FaceTec {Engine} codID 200 para JourneyId {JourneyId}, mas document/result não foi obtido. Usando o retorno do SDK como contingência.",
                engine,
                journey.Id
            );

            using var fallbackDocument = JsonDocument.Parse(sdkResult.GetRawText());
            await repository.MarkJourneyCompletedAsync(journey.Id, fallbackDocument, deviceLocationJson, "completo", cancellationToken, JourneyStatus.COMPLETED);
            await EnqueueTenantWebhookAsync(journey, appkey, requestKey, fallbackDocument.RootElement, deviceLocationJson, dependencies, cancellationToken);
        }
    }

    private static async Task StoreFrontalFaceIfPresentAsync(
        JourneySession journey,
        JsonElement certifaceResult,
        SubmissionDependencies dependencies,
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
            var asset = await dependencies.FaceStorage.SaveFrontalFaceAsync(journey.Tenant.Id, journey.Id, frontalFaceBase64, cancellationToken);
            await dependencies.Repository.UpsertJourneyFaceAssetAsync(journey.Id, journey.Tenant.Id, "FRONTAL", asset, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao armazenar a face frontal da jornada {JourneyId}.", journey.Id);
        }
    }

    private static async Task EnqueueTenantWebhookAsync(
        JourneySession journey,
        string appkey,
        string requestKey,
        JsonElement providerResult,
        string? deviceLocationJson,
        SubmissionDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var payload = TenantCallbackPayloadFactory.Create(journey, appkey, providerResult, deviceLocationJson);
        await dependencies.Outbox.EnqueueAsync(journey.Id, $"sdk:{requestKey}", payload, cancellationToken);
    }

    private static async Task<AppkeyAccess> AuthorizeAppkeyAsync(
        JsonElement payload,
        HttpContext httpContext,
        AwfaceRepository repository,
        SignedTokenService tokens,
        CancellationToken cancellationToken)
    {
        var appkey = CertifaceClient.GetRequiredString(payload, "appkey");
        var journey = await repository.GetJourneyByAppkeyAsync(appkey, cancellationToken);
        if (journey is null)
        {
            return new AppkeyAccess(null, null, Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Appkey não encontrada",
                detail: "A appkey informada não pertence a nenhuma jornada AWFace."
            ));
        }

        if (!EndpointAccess.IsJourneyAuthorized(httpContext, tokens, journey.Id))
        {
            return new AppkeyAccess(null, null, EndpointAccess.JourneyUnauthorized());
        }

        return new AppkeyAccess(journey, appkey, null);
    }

    private static void LogProviderFailure(CertifaceProxyResponse response, JsonElement payload, string operation, ILogger logger)
    {
        if (response.StatusCode < 400)
        {
            return;
        }

        var body = response.Body ?? "";
        logger.LogWarning(
            "Certiface {Operation} retornou {StatusCode}. Campos recebidos pelo AWFace: {PayloadKeys}. Body (truncado): {Body}",
            operation,
            response.StatusCode,
            string.Join(",", payload.EnumerateObject().Select(item => item.Name)),
            body.Length > MaxLoggedBodyLength ? body[..MaxLoggedBodyLength] + "…" : body
        );
    }

    private static bool TryParseProviderResponse(string? responseBody, out JsonDocument providerResult)
    {
        providerResult = null!;
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            providerResult = JsonDocument.Parse(responseBody);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetCodId(JsonElement response, out double codId)
    {
        codId = 0;
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("codID", out var codIdElement))
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
            || deviceLocation.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return deviceLocation.GetRawText();
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record AppkeyAccess(JourneySession? Journey, string? Appkey, IResult? Result);
}

/// <summary>Dependências agrupadas das submissões do SDK (injetadas via [AsParameters]-like DI).</summary>
public sealed class SubmissionDependencies
{
    public SubmissionDependencies(
        CertifaceClient certiface,
        AwfaceRepository repository,
        CallbackOutbox outbox,
        FaceAssetStorage faceStorage,
        SignedTokenService tokens,
        ILoggerFactory loggerFactory)
    {
        Certiface = certiface;
        Repository = repository;
        Outbox = outbox;
        FaceStorage = faceStorage;
        Tokens = tokens;
        LoggerFactory = loggerFactory;
    }

    public CertifaceClient Certiface { get; }
    public AwfaceRepository Repository { get; }
    public CallbackOutbox Outbox { get; }
    public FaceAssetStorage FaceStorage { get; }
    public SignedTokenService Tokens { get; }
    public ILoggerFactory LoggerFactory { get; }
}
