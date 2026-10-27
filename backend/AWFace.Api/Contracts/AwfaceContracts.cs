using System.Text.Json.Serialization;
using AWFace.Api.Domain;

namespace AWFace.Api.Contracts;

public sealed record AdminLoginRequest(string Email, string Password);

public sealed record AdminLoginResponse(bool Authenticated, string AccessToken, DateTimeOffset ExpiresAt, string Email);

/// <summary>
/// Credencial do provedor recebida do admin. <see cref="ProviderPass"/> vazio mantém a senha já cadastrada.
/// </summary>
public sealed record TenantCredentialDto(
    JourneyType JourneyType,
    string ProviderUser,
    string? ProviderPass
);

/// <summary>
/// Cadastro de Tenant. Segredos vazios (<c>providerPass</c>, <c>callbackOAuthClientSecret</c>) mantêm o valor atual.
/// </summary>
public sealed record TenantUpsertRequest(
    Guid? Id,
    string Name,
    string? IntegrationToken,
    TenantStatus Status,
    string TermsUrl,
    string PrivacyUrl,
    string? LogoBase64,
    string? Theme,
    string? PrimaryColor,
    string? SecondaryColor,
    string CallbackUrl,
    string SecureCallbackToken,
    bool CallbackOAuthEnabled,
    string? CallbackOAuthTokenUrl,
    string? CallbackOAuthClientId,
    string? CallbackOAuthClientSecret,
    IReadOnlyList<TenantCredentialDto> Credentials
);

public sealed record TenantStatusRequest(TenantStatus Status);

/// <summary>Credencial devolvida ao admin: a senha do provedor nunca sai da API.</summary>
public sealed record TenantCredentialAdminDto(
    JourneyType JourneyType,
    string ProviderUser,
    bool HasProviderPass
);

/// <summary>Tenant devolvido ao admin autenticado, sem senhas do provedor nem client secret OAuth.</summary>
public sealed record TenantAdminResponse(
    Guid Id,
    string Name,
    string IntegrationToken,
    TenantStatus Status,
    string TermsUrl,
    string PrivacyUrl,
    string? LogoBase64,
    string Theme,
    string PrimaryColor,
    string SecondaryColor,
    string CallbackUrl,
    string SecureCallbackToken,
    bool CallbackOAuthEnabled,
    string? CallbackOAuthTokenUrl,
    string? CallbackOAuthClientId,
    bool HasCallbackOAuthClientSecret,
    IReadOnlyList<TenantCredentialAdminDto> Credentials,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public static TenantAdminResponse From(Tenant tenant) => new(
        tenant.Id,
        tenant.Name,
        tenant.IntegrationToken,
        tenant.Status,
        tenant.TermsUrl,
        tenant.PrivacyUrl,
        tenant.LogoBase64,
        tenant.Theme,
        tenant.PrimaryColor,
        tenant.SecondaryColor,
        tenant.CallbackUrl,
        tenant.SecureCallbackToken,
        tenant.CallbackOAuthEnabled,
        tenant.CallbackOAuthTokenUrl,
        tenant.CallbackOAuthClientId,
        !string.IsNullOrWhiteSpace(tenant.CallbackOAuthClientSecret),
        tenant.Credentials
            .Select(item => new TenantCredentialAdminDto(item.JourneyType, item.ProviderUser, !string.IsNullOrWhiteSpace(item.ProviderPass)))
            .ToList(),
        tenant.CreatedAt,
        tenant.UpdatedAt
    );
}

public sealed record JourneyStartRequest(
    string IntegrationToken,
    JourneyType JourneyType,
    string Cpf,
    string FullName,
    DateOnly BirthDate,
    string ExternalClientId
);

public sealed record JourneyLaunchRequest(
    string IntegrationToken,
    JourneyType JourneyType,
    string Cpf,
    string FullName,
    DateOnly BirthDate,
    string ExternalClientId,
    string? HostReference,
    object? Metadata
);

public sealed record JourneyLaunchResponse(
    Guid JourneyId,
    string LaunchToken,
    string LaunchUrl,
    DateTimeOffset ExpiresAt
);

/// <summary>Dados públicos do Tenant necessários para a experiência do usuário final.</summary>
public sealed record JourneyTenantPublicDto(
    Guid Id,
    string Name,
    string TermsUrl,
    string PrivacyUrl,
    string Theme,
    string PrimaryColor,
    string SecondaryColor
);

public sealed record JourneySubjectPublicDto(string FullName);

/// <summary>
/// Sessão da jornada devolvida ao navegador. Não contém CPF, data de nascimento,
/// credenciais do provedor nem segredos de webhook do Tenant.
/// </summary>
public sealed record JourneySessionResponse(
    Guid Id,
    JourneyTenantPublicDto Tenant,
    JourneyType JourneyType,
    LivenessEngine LivenessEngine,
    JourneySubjectPublicDto Subject,
    JourneyStatus Status,
    DateTimeOffset? ConsentAt,
    DateTimeOffset? RefusalAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SessionToken,
    DateTimeOffset? SessionTokenExpiresAt
)
{
    public static JourneySessionResponse From(JourneySession journey, Security.IssuedToken? sessionToken = null) => new(
        journey.Id,
        new JourneyTenantPublicDto(
            journey.Tenant.Id,
            journey.Tenant.Name,
            journey.Tenant.TermsUrl,
            journey.Tenant.PrivacyUrl,
            journey.Tenant.Theme,
            journey.Tenant.PrimaryColor,
            journey.Tenant.SecondaryColor
        ),
        journey.JourneyType,
        journey.LivenessEngine,
        new JourneySubjectPublicDto(journey.Subject.FullName),
        journey.Status,
        journey.ConsentAt,
        journey.RefusalAt,
        journey.CreatedAt,
        journey.UpdatedAt,
        sessionToken?.Token,
        sessionToken?.ExpiresAt
    );
}

public sealed record JourneyLaunchResolveResponse(JourneySessionResponse Session);

public sealed record ConsentRequest(ConsentDecision Decision);

public sealed record AppkeyResponse(string Appkey);

public sealed record CertifaceCredentialResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires")] string Expires
);
