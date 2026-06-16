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
            var journey = await repository.CreateJourneyAsync(tenant, request, userAgent, cancellationToken);
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
            var reusableAppkey = await repository.GetReusableAppkeyAsync(journeyId, appkeyLifetime, cancellationToken);
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

            var success = callback.ResponseStatus is >= 200 and <= 299;
            return Results.Ok(new
            {
                status = success ? "SUCCESS" : "FAILED",
                callbackStatus = callback.ResponseStatus,
                message = success
                    ? "" //"Prova de vida concluída com sucesso."
                    : "A prova de vida foi concluída, mas houve falha ao comunicar o sistema de assinatura.",
                deliveredAt = callback.DeliveredAt
            });
        });

        return app;
    }
}
