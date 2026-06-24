using System.Security.Cryptography;
using System.Text;
using AWFace.Api.Configuration;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using AWFace.Api.Services;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Endpoints;

public static class JourneyLaunchEndpoints
{
    private static readonly TimeSpan LaunchTokenLifetime = TimeSpan.FromMinutes(20);

    public static IEndpointRouteBuilder MapJourneyLaunchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/journey-launches").WithTags("AWFace Journey Launches");

        group.MapPost("/", async (
            JourneyLaunchRequest request,
            HttpContext httpContext,
            AwfaceRepository repository,
            IOptions<AwfaceOptions> options,
            CancellationToken cancellationToken) =>
        {
            var startRequest = ToStartRequest(request);
            var errors = JourneyValidator.Validate(startRequest);
            if (errors.Count > 0)
            {
                return Results.BadRequest(errors);
            }

            var tenant = await repository.GetTenantByTokenAsync(request.IntegrationToken, cancellationToken);
            if (tenant is null)
            {
                return Results.BadRequest(new[] { new { field = "integrationToken", message = "Token de integraÃ§Ã£o nÃ£o encontrado." } });
            }

            if (tenant.Status != TenantStatus.ACTIVE)
            {
                return Results.BadRequest(new[] { new { field = "integrationToken", message = "Tenant bloqueado ou cancelado." } });
            }

            if (!tenant.Credentials.Any(item => item.JourneyType == request.JourneyType))
            {
                return Results.BadRequest(new[] { new { field = "journeyType", message = "Tipo de jornada nÃ£o habilitado para este tenant." } });
            }

            var launchToken = CreateLaunchToken();
            var launchTokenHash = HashLaunchToken(launchToken);
            var expiresAt = DateTimeOffset.UtcNow.Add(LaunchTokenLifetime);
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();

            var journey = await repository.CreateJourneyLaunchAsync(
                tenant,
                request,
                launchTokenHash,
                expiresAt,
                options.Value.LivenessEngine,
                userAgent,
                cancellationToken
            );

            return Results.Ok(new JourneyLaunchResponse(
                journey.Id,
                launchToken,
                BuildLaunchUrl(options.Value.FrontendBaseUrl, launchToken),
                expiresAt
            ));
        });

        group.MapPost("/{launchToken}/consume", async (
            string launchToken,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(launchToken))
            {
                return Results.BadRequest(new { message = "Token de lançamento não informado." });
            }

            var journey = await repository.ConsumeJourneyLaunchAsync(HashLaunchToken(launchToken), cancellationToken);
            if (journey is null)
            {
                return Results.NotFound(new { message = "Token de lançamento inválido ou expirado." });
            }

            return Results.Ok(new JourneyLaunchResolveResponse(journey));
        });

        return app;
    }

    private static JourneyStartRequest ToStartRequest(JourneyLaunchRequest request)
    {
        return new JourneyStartRequest(
            request.IntegrationToken,
            request.JourneyType,
            request.Cpf,
            request.FullName,
            request.BirthDate,
            request.ExternalClientId
        );
    }

    private static string CreateLaunchToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashLaunchToken(string launchToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(launchToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildLaunchUrl(string frontendBaseUrl, string launchToken)
    {
        var normalizedBaseUrl = frontendBaseUrl.TrimEnd('/');
        return $"{normalizedBaseUrl}/#/journey-launch?token={Uri.EscapeDataString(launchToken)}";
    }
}
