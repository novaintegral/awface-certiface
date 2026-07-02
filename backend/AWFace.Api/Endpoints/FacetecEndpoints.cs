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
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AWFace.FaceTec.V9.Liveness");
            var response = await certiface.ProcessV9LivenessAsync(payload, cancellationToken);

            LogProviderFailure(response, payload, "V9 liveness", logger);
            await RegisterCompletionOrRejectionAsync(
                payload,
                response,
                LivenessEngine.V9,
                repository,
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
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AWFace.FaceTec.V10.ProcessRequest");
        var response = await certiface.Process3dRequestAsync(payload, cancellationToken);

        LogProviderFailure(response, payload, "V10 process-request", logger);
        await RegisterCompletionOrRejectionAsync(
            payload,
            response,
            LivenessEngine.V10,
            repository,
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

    private static async Task RegisterCompletionOrRejectionAsync(
        JsonElement requestPayload,
        CertifaceProxyResponse response,
        LivenessEngine engine,
        AwfaceRepository repository,
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

        var deviceLocationJson = ExtractDeviceLocationJson(requestPayload);
        if (IsInvalidLivenessResponse(response.Body))
        {
            await repository.RegisterRejectedLivenessAsync(
                journey.Id,
                appkey,
                engine,
                response.Body,
                deviceLocationJson,
                cancellationToken
            );

            logger.LogInformation(
                "Resposta FaceTec {Engine} 300.1 registrada para JourneyId {JourneyId}. "
                + "A jornada permanecera apta para retentativa sem consultar document/result.",
                engine,
                journey.Id
            );
            return;
        }

        await repository.RegisterLivenessSubmissionAsync(
            journey.Id,
            appkey,
            engine,
            "{}",
            deviceLocationJson,
            cancellationToken
        );

        logger.LogInformation(
            "Tentativa FaceTec {Engine} registrada para JourneyId {JourneyId}. "
            + "O resultado da jornada sera persistido somente apos webhook Certiface e consulta ao document/result para codID 200 ou 300.2.",
            engine,
            journey.Id
        );
    }

    private static bool IsInvalidLivenessResponse(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return TryGetCodId(document.RootElement, out var codId) && codId == 300.1d;
        }
        catch (JsonException)
        {
            return false;
        }
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
