using System.Net.Http.Json;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
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
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var journey = await repository.GetJourneyByAppkeyAsync(request.Appkey, cancellationToken);
            if (journey is null)
            {
                return Results.NotFound(new { message = "Appkey não associada a uma jornada AWFace." });
            }

            using var result = await certiface.GetDocumentResultAsync(request.Appkey, cancellationToken);
            await repository.MarkJourneyCompletedAsync(journey.Id, result, cancellationToken);

            var callbackPayload = new
            {
                status = request.Status,
                appkey = request.Appkey,
                journeyId = journey.Id,
                tenantId = journey.Tenant.Id,
                idExternoCliente = journey.Subject.ExternalClientId,
                result = result.RootElement.Clone()
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
}
