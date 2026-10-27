using AWFace.Api.Configuration;
using AWFace.Api.Contracts;
using AWFace.Api.Data;
using AWFace.Api.Security;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Endpoints;

public static class AdminEndpoints
{
    public const string LoginRateLimitPolicy = "admin-login";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var publicGroup = app.MapGroup("/api/awface/admin").WithTags("AWFace Admin");

        publicGroup.MapPost("/login", async (
            AdminLoginRequest request,
            AwfaceRepository repository,
            SignedTokenService tokens,
            IOptions<AwfaceOptions> options,
            ILoggerFactory loggerFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AWFace.Admin.Login");
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            {
                return EndpointAccess.Unauthorized("Login ou senha inválidos.");
            }

            var admin = await repository.GetAdminByEmailAsync(request.Email, cancellationToken);
            if (admin is null)
            {
                PasswordHasher.SimulateVerification(request.Password);
            }

            if (admin is null || !admin.Active || !PasswordHasher.Verify(request.Password, admin.PasswordHash))
            {
                logger.LogWarning("Falha de login administrativo. Ip={RemoteIp}.", httpContext.Connection.RemoteIpAddress);
                return EndpointAccess.Unauthorized("Login ou senha inválidos.");
            }

            var lifetime = TimeSpan.FromMinutes(Math.Clamp(options.Value.Security.AdminTokenLifetimeMinutes, 5, 720));
            var token = tokens.Issue(SignedTokenService.AdminPurpose, admin.Email, lifetime);
            logger.LogInformation("Login administrativo realizado. AdminId={AdminId}.", admin.Id);
            return Results.Ok(new AdminLoginResponse(true, token.Token, token.ExpiresAt, admin.Email));
        }).RequireRateLimiting(LoginRateLimitPolicy);

        var group = app.MapGroup("/api/awface/admin")
            .WithTags("AWFace Admin")
            .AddEndpointFilter<AdminAuthorizationFilter>();

        group.MapGet("/me", (HttpContext httpContext) =>
            Results.Ok(new { email = httpContext.Items[AdminAuthorizationFilter.AdminEmailItem] }));

        group.MapGet("/tenants", async (AwfaceRepository repository, CancellationToken cancellationToken) =>
        {
            var tenants = await repository.ListTenantsAsync(cancellationToken);
            return Results.Ok(tenants.Select(TenantAdminResponse.From));
        });

        group.MapPost("/tenants", async (TenantUpsertRequest request, AwfaceRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = ValidateTenant(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(errors);
            }

            var tenant = await repository.UpsertTenantAsync(request, cancellationToken);
            return Results.Ok(TenantAdminResponse.From(tenant));
        });

        group.MapPatch("/tenants/{tenantId:guid}/status", async (
            Guid tenantId,
            TenantStatusRequest request,
            AwfaceRepository repository,
            CancellationToken cancellationToken) =>
        {
            var tenant = await repository.UpdateTenantStatusAsync(tenantId, request.Status, cancellationToken);
            return tenant is null ? Results.NotFound() : Results.Ok(TenantAdminResponse.From(tenant));
        });

        return app;
    }

    private static List<object> ValidateTenant(TenantUpsertRequest request)
    {
        var errors = new List<object>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new { field = "name", message = "Informe o nome do tenant." });
        }

        if (!IsHttpUrl(request.CallbackUrl))
        {
            errors.Add(new { field = "callbackUrl", message = "Informe uma URL de callback absoluta (http ou https)." });
        }

        if (string.IsNullOrWhiteSpace(request.SecureCallbackToken) || request.SecureCallbackToken.Trim().Length < 16)
        {
            errors.Add(new { field = "secureCallbackToken", message = "O token de callback deve ter ao menos 16 caracteres; ele também assina o webhook (HMAC)." });
        }

        if (!IsHttpUrl(request.TermsUrl))
        {
            errors.Add(new { field = "termsUrl", message = "Informe a URL dos termos de uso." });
        }

        if (!IsHttpUrl(request.PrivacyUrl))
        {
            errors.Add(new { field = "privacyUrl", message = "Informe a URL da política de privacidade." });
        }

        if (request.CallbackOAuthEnabled && !IsHttpUrl(request.CallbackOAuthTokenUrl))
        {
            errors.Add(new { field = "callbackOAuthTokenUrl", message = "Informe a URL de token OAuth2." });
        }

        if (request.Credentials.GroupBy(item => item.JourneyType).Any(group => group.Count() > 1))
        {
            errors.Add(new { field = "credentials", message = "Cada tipo de jornada pode ter apenas uma credencial." });
        }

        if (request.Credentials.Any(item => string.IsNullOrWhiteSpace(item.ProviderUser)))
        {
            errors.Add(new { field = "credentials", message = "Informe o usuário do provedor em todas as credenciais." });
        }

        return errors;
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
