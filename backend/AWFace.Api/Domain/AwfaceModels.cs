namespace AWFace.Api.Domain;

public enum TenantStatus
{
    ACTIVE,
    BLOCKED,
    CANCELLED
}

public enum JourneyType
{
    LIVENESS,
    LIVENESS_FACE_BUREAU,
    LIVENESS_FACE_BUREAU_DOCUMENT
}

public enum JourneyStatus
{
    CREATED,
    CONSENT_ACCEPTED,
    CONSENT_REFUSED,
    APPKEY_CREATED,
    LIVENESS_STARTED,
    COMPLETED,
    FAILED
}

public enum ConsentDecision
{
    ACCEPTED,
    REFUSED
}

public sealed record TenantCredential(
    Guid Id,
    JourneyType JourneyType,
    string ProviderUser,
    string ProviderPass
);

public sealed record Tenant(
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
    string? CallbackOAuthClientSecret,
    IReadOnlyList<TenantCredential> Credentials,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record JourneySubject(
    string Cpf,
    string FullName,
    DateOnly BirthDate,
    string ExternalClientId
);

public sealed record JourneySession(
    Guid Id,
    Tenant Tenant,
    JourneyType JourneyType,
    JourneySubject Subject,
    JourneyStatus Status,
    string? Appkey,
    DateTimeOffset? ConsentAt,
    DateTimeOffset? RefusalAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record CallbackDeliveryStatus(
    int? ResponseStatus,
    string? ResponseBody,
    DateTimeOffset DeliveredAt
);

public sealed record JourneyFaceAsset(
    Guid JourneyId,
    string AssetType,
    string StorageKey,
    string ContentType,
    string Sha256,
    long SizeBytes,
    string EncryptionAlgorithm,
    DateTimeOffset CreatedAt
);
