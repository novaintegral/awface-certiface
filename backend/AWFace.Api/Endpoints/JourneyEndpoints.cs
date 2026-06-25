using AWFace.Api.Contracts;
using AWFace.Api.Configuration;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Endpoints;

public static class JourneyEndpoints
{
    public static IEndpointRouteBuilder MapJourneyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/journeys").WithTags("AWFace Journeys");

        group.MapPost("/", async (
            JourneyStartRequest request,
            HttpContext httpContext,
            AwfaceRepository repository,
            IOptions<AwfaceOptions> options,
            CancellationToken cancellationToken) =>
        {
            var errors = JourneyValidator.Validate(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(errors);
            }

            var tenant = await repository.GetTenantByTokenAsync(request.IntegrationToken, cancellationToken);
            if (tenant is null)
            {
                return Results.BadRequest(new[] { new { field = "integrationToken", message = "Token de integração não encontrado." } });
            }

            if (tenant.Status != TenantStatus.ACTIVE)
            {
                return Results.BadRequest(new[] { new { field = "integrationToken", message = "Tenant bloqueado ou cancelado." } });
            }

            if (!tenant.Credentials.Any(item => item.JourneyType == request.JourneyType))
            {
                return Results.BadRequest(new[] { new { field = "journeyType", message = "Tipo de jornada não habilitado para este tenant." } });
            }

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var journey = await repository.CreateJourneyAsync(
                tenant,
                request,
                options.Value.LivenessEngine,
                userAgent,
                cancellationToken
            );
            return Results.Ok(journey);
        });

        group.MapPost("/{journeyId:guid}/consent", async (
            Guid journeyId,
            ConsentRequest request,
            HttpContext httpContext,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            var journey = await repository.GetJourneyByIdAsync(journeyId, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound();
            }

            var updated = await repository.RegisterConsentAsync(
                journeyId,
                request.Decision,
                httpContext.Request.Headers.UserAgent.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken
            );

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapPost("/{journeyId:guid}/appkey", async (
            Guid journeyId,
            bool? force,
            AwfaceRepository repository,
            CertifaceClient certiface,
            IOptions<AwfaceOptions> options,
            CancellationToken cancellationToken) =>
        {
            var journey = await repository.GetJourneyByIdAsync(journeyId, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound();
            }

            if (journey.Status != JourneyStatus.CONSENT_ACCEPTED && journey.Status != JourneyStatus.APPKEY_CREATED)
            {
                return Results.BadRequest(new { message = "A jornada precisa de consentimento aceito antes da criação da appkey." });
            }

            var appkeyLifetime = TimeSpan.FromMinutes(Math.Max(1, options.Value.LivenessAppkeyLifetimeMinutes));
            var reusableAppkey = force == true
                ? null
                : await repository.GetReusableAppkeyAsync(journeyId, appkeyLifetime, cancellationToken);
            if (!string.IsNullOrWhiteSpace(reusableAppkey))
            {
                return Results.Ok(new AppkeyResponse(reusableAppkey));
            }

            try
            {
                var appkey = await certiface.CreateAppkeyAsync(journey, cancellationToken);
                await repository.SetJourneyAppkeyAsync(journeyId, appkey, cancellationToken);
                return Results.Ok(new AppkeyResponse(appkey));
            }
            catch (CertifaceProviderException exception)
            {
                return Results.Json(
                    new
                    {
                        message = "Não foi possível obter a appkey na Certiface.",
                        providerOperation = exception.Operation,
                        providerStatus = exception.StatusCode
                    },
                    statusCode: StatusCodes.Status502BadGateway
                );
            }
        });

        group.MapGet("/{journeyId:guid}/completion", async (
            Guid journeyId,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            var journey = await repository.GetJourneyByIdAsync(journeyId, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound();
            }

            var callback = await repository.GetLatestCallbackDeliveryAsync(journeyId, cancellationToken);
            if (callback is null)
            {
                return Results.Ok(new
                {
                    status = "PENDING",
                    message = "A prova de vida foi enviada e está em processo de validação."
                });
            }

            var providerFailed = journey.Status == JourneyStatus.FAILED;
            var success = !providerFailed && callback.ResponseStatus is >= 200 and <= 299;
            return Results.Ok(new
            {
                status = success ? "SUCCESS" : "FAILED",
                callbackStatus = callback.ResponseStatus,
                message = success
                    ? ""
                    : providerFailed
                        ? "O provedor informou erro ao concluir a prova de vida."
                        : "A prova de vida foi concluída, mas houve falha ao comunicar o sistema de assinatura.",
                deliveredAt = callback.DeliveredAt
            });
        });

        group.MapGet("/{journeyId:guid}/tenant-logo", async (
            Guid journeyId,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            var journey = await repository.GetJourneyByIdAsync(journeyId, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound();
            }

            var logo = ParseImageBase64(journey.Tenant.LogoBase64);
            if (logo is null)
            {
                return Results.NotFound();
            }

            return Results.File(logo.Value.Bytes, logo.Value.ContentType);
        });

        group.MapGet("/{journeyId:guid}/result", async (
            Guid journeyId,
            HttpContext httpContext,
            AwfaceRepository repository,
            CertifaceClient certiface,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeHostJourneyAsync(journeyId, httpContext, repository, cancellationToken);
            if (authorization.Result is not null)
            {
                return authorization.Result;
            }

            var journey = authorization.Journey!;
            if (string.IsNullOrWhiteSpace(journey.Appkey))
            {
                return Results.BadRequest(new { message = "A jornada ainda não possui appkey para consulta do resultado." });
            }

            try
            {
                using var document = await certiface.GetDocumentResultAsync(journey.Appkey, cancellationToken);
                return Results.Content(document.RootElement.GetRawText(), "application/json");
            }
            catch (CertifaceProviderException exception)
            {
                return Results.Json(
                    new
                    {
                        message = "Não foi possível consultar o resultado da prova de vida na Certiface.",
                        providerOperation = exception.Operation,
                        providerStatus = exception.StatusCode
                    },
                    statusCode: StatusCodes.Status502BadGateway
                );
            }
        });

        group.MapGet("/{journeyId:guid}/face-image", async (
            Guid journeyId,
            HttpContext httpContext,
            AwfaceRepository repository,
            FaceAssetStorage faceStorage,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeHostJourneyAsync(journeyId, httpContext, repository, cancellationToken);
            if (authorization.Result is not null)
            {
                return authorization.Result;
            }

            var faceAsset = await repository.GetJourneyFaceAssetAsync(journeyId, "FRONTAL", cancellationToken);
            if (faceAsset is null)
            {
                return Results.NotFound(new { message = "Imagem da face não encontrada para esta jornada." });
            }

            var faceContent = await faceStorage.ReadAsync(faceAsset.StorageKey, faceAsset.ContentType, cancellationToken);
            if (faceContent is null)
            {
                return Results.NotFound(new { message = "Arquivo da imagem da face não encontrado no storage AWFace." });
            }

            var imageBase64 = Convert.ToBase64String(faceContent.Bytes);
            return Results.Ok(new
            {
                journeyId = faceAsset.JourneyId,
                assetType = faceAsset.AssetType,
                contentType = faceAsset.ContentType,
                imageBase64,
                encoding = "base64",
                sha256 = faceAsset.Sha256,
                sizeBytes = faceAsset.SizeBytes,
                encryptionAlgorithm = faceAsset.EncryptionAlgorithm,
                createdAt = faceAsset.CreatedAt
            });
        });

        group.MapGet("/{journeyId:guid}/face-image/file", async (
            Guid journeyId,
            HttpContext httpContext,
            AwfaceRepository repository,
            FaceAssetStorage faceStorage,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeHostJourneyAsync(journeyId, httpContext, repository, cancellationToken);
            if (authorization.Result is not null)
            {
                return authorization.Result;
            }

            var faceAsset = await repository.GetJourneyFaceAssetAsync(journeyId, "FRONTAL", cancellationToken);
            if (faceAsset is null)
            {
                return Results.NotFound(new { message = "Imagem da face não encontrada para esta jornada." });
            }

            var faceContent = await faceStorage.ReadAsync(faceAsset.StorageKey, faceAsset.ContentType, cancellationToken);
            if (faceContent is null)
            {
                return Results.NotFound(new { message = "Arquivo da imagem da face não encontrado no storage AWFace." });
            }

            var extension = faceAsset.ContentType == "image/png" ? "png" : "jpg";
            return Results.File(
                faceContent.Bytes,
                faceAsset.ContentType,
                $"{journeyId:N}-frontal.{extension}"
            );
        });

        return app;
    }

    private static async Task<HostJourneyAuthorization> AuthorizeHostJourneyAsync(
        Guid journeyId,
        HttpContext httpContext,
        AwfaceRepository repository,
        CancellationToken cancellationToken)
    {
        var integrationToken = GetIntegrationToken(httpContext);
        if (string.IsNullOrWhiteSpace(integrationToken))
        {
            return new HostJourneyAuthorization(null, Results.Unauthorized());
        }

        var tenant = await repository.GetTenantByTokenAsync(integrationToken, cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.ACTIVE)
        {
            return new HostJourneyAuthorization(null, Results.Unauthorized());
        }

        var journey = await repository.GetJourneyByIdAsync(journeyId, cancellationToken);
        if (journey is null)
        {
            return new HostJourneyAuthorization(null, Results.NotFound());
        }

        if (journey.Tenant.Id != tenant.Id)
        {
            return new HostJourneyAuthorization(null, Results.Forbid());
        }

        return new HostJourneyAuthorization(journey, null);
    }

    private static string? GetIntegrationToken(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("X-AWFace-Integration-Token", out var headerValue))
        {
            return headerValue.ToString();
        }

        if (httpContext.Request.Query.TryGetValue("integrationToken", out var queryValue))
        {
            return queryValue.ToString();
        }

        return null;
    }

    private static ParsedImage? ParseImageBase64(string? value)
    {
        var logo = value?.Trim();
        if (string.IsNullOrWhiteSpace(logo))
        {
            return null;
        }

        var contentType = "image/png";
        var base64 = logo;
        const string dataPrefix = "data:";
        if (logo.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = logo.IndexOf(',');
            if (commaIndex < 0)
            {
                return null;
            }

            var metadata = logo[dataPrefix.Length..commaIndex];
            var semicolonIndex = metadata.IndexOf(';');
            contentType = semicolonIndex > 0 ? metadata[..semicolonIndex] : metadata;
            base64 = logo[(commaIndex + 1)..];
        }

        try
        {
            return new ParsedImage(Convert.FromBase64String(base64), contentType);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record HostJourneyAuthorization(JourneySession? Journey, IResult? Result);
    private readonly record struct ParsedImage(byte[] Bytes, string ContentType);
}
