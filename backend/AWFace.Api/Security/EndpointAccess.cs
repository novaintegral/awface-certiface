namespace AWFace.Api.Security;

/// <summary>Exige um token de administrador válido no header <c>Authorization: Bearer</c>.</summary>
public sealed class AdminAuthorizationFilter : IEndpointFilter
{
    public const string AdminEmailItem = "awface.admin.email";

    private readonly SignedTokenService _tokens;

    public AdminAuthorizationFilter(SignedTokenService tokens)
    {
        _tokens = tokens;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var adminEmail = _tokens.Validate(EndpointAccess.GetBearerToken(httpContext), SignedTokenService.AdminPurpose);
        if (adminEmail is null)
        {
            return EndpointAccess.Unauthorized("Sessão administrativa ausente ou expirada.");
        }

        httpContext.Items[AdminEmailItem] = adminEmail;
        return await next(context);
    }
}

public static class EndpointAccess
{
    public const string JourneyTokenHeader = "X-AWFace-Journey-Token";

    public static string? GetBearerToken(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[scheme.Length..].Trim()
            : null;
    }

    /// <summary>Verifica se o token de sessão da jornada enviado pelo navegador pertence à jornada informada.</summary>
    public static bool IsJourneyAuthorized(HttpContext httpContext, SignedTokenService tokens, Guid journeyId)
    {
        var token = httpContext.Request.Headers[JourneyTokenHeader].ToString();
        var subject = tokens.Validate(token, SignedTokenService.JourneyPurpose);
        return subject is not null && Guid.TryParse(subject, out var tokenJourneyId) && tokenJourneyId == journeyId;
    }

    public static IResult Unauthorized(string message)
    {
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Não autorizado", detail: message);
    }

    public static IResult Forbidden(string message)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Acesso negado", detail: message);
    }

    public static IResult JourneyUnauthorized()
    {
        return Unauthorized("Sessão da jornada ausente, inválida ou expirada. Solicite um novo link de acesso.");
    }
}
