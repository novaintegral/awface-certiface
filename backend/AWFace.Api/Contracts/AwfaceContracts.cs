using System.Text.Json.Serialization;
using AWFace.Api.Domain;

namespace AWFace.Api.Contracts;

public sealed record AdminLoginRequest(string Email, string Password);

public sealed record TenantCredentialDto(
    JourneyType JourneyType,
    string ProviderUser,
    string ProviderPass
);

public sealed record TenantUpsertRequest(
    Guid? Id,
    string Name,
    string? IntegrationToken,
    TenantStatus Status,
    string TermsUrl,
    string PrivacyUrl,
    string? LogoBase64,
    string CallbackUrl,
    string SecureCallbackToken,
    IReadOnlyList<TenantCredentialDto> Credentials
);

public sealed record TenantStatusRequest(TenantStatus Status);

public sealed record JourneyStartRequest(
    string IntegrationToken,
    JourneyType JourneyType,
    string Cpf,
    string FullName,
    DateOnly BirthDate,
    string ExternalClientId
);

public sealed record ConsentRequest(ConsentDecision Decision);

public sealed record AppkeyResponse(string Appkey);

public sealed record CertifaceCredentialResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires")] string Expires
);

public sealed record CertifaceWebhookRequest(
    [property: JsonPropertyName("Status")] string Status,
    [property: JsonPropertyName("Appkey")] string Appkey
);
