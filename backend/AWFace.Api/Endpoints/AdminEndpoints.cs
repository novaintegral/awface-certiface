using AWFace.Api.Configuration;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/awface/admin").WithTags("AWFace Admin");

        group.MapPost("/login", (AdminLoginRequest request, IOptions<AwfaceOptions> options) =>
        {
            var admin = options.Value.Admin;
            var ok = string.Equals(request.Email, admin.Email, StringComparison.OrdinalIgnoreCase) &&
                     request.Password == admin.Password;

            return ok ? Results.Ok(new { authenticated = true }) : Results.Unauthorized();
        });

        group.MapGet("/tenants", async (AwfaceRepository repository, CancellationToken cancellationToken) =>
        {
            return Results.Ok(await repository.ListTenantsAsync(cancellationToken));
        });

        group.MapPost("/tenants", async (TenantUpsertRequest request, AwfaceRepository repository, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new[] { new { field = "name", message = "Informe o nome do tenant." } });
            }

            var tenant = await repository.UpsertTenantAsync(request, cancellationToken);
            return Results.Ok(tenant);
        });

        group.MapPatch("/tenants/{tenantId:guid}/status", async (
            Guid tenantId,
            TenantStatusRequest request,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            var tenant = await repository.UpdateTenantStatusAsync(tenantId, request.Status, cancellationToken);
            return tenant is null ? Results.NotFound() : Results.Ok(tenant);
        });

        return app;
    }
}
