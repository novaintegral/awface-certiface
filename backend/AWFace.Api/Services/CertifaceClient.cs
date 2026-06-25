using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AWFace.Api.Configuration;
using AWFace.Api.Contracts;
using AWFace.Api.Domain;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Services;

public sealed class CertifaceClient
{
    private const int MaxProcessRequestTransientRetries = 2;
    private static readonly TimeSpan ProcessRequestRetryDelay = TimeSpan.FromMilliseconds(750);

    private readonly HttpClient _httpClient;
    private readonly CertifaceOptions _options;
    private readonly ILogger<CertifaceClient> _logger;

    public CertifaceClient(HttpClient httpClient, IOptions<CertifaceOptions> options, ILogger<CertifaceClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> CreateAppkeyAsync(JourneySession journey, CancellationToken cancellationToken)
    {
        var credential = journey.Tenant.Credentials.FirstOrDefault(item => item.JourneyType == journey.JourneyType)
            ?? throw new InvalidOperationException("Credencial Certiface não configurada para esta jornada.");

        _logger.LogInformation("Obtendo token Certiface para JourneyId {JourneyId} e TenantId {TenantId}.", journey.Id, journey.Tenant.Id);
        var token = await GetCredentialTokenAsync(credential, cancellationToken);
        _logger.LogInformation(
            "Token Certiface obtido, expires={Expires}. Gerando appkey para JourneyId {JourneyId}, ExternalClientId {ExternalClientId}.",
            token.Expires,
            journey.Id,
            journey.Subject.ExternalClientId
        );

        var serializedToken = JsonSerializer.Serialize(new
        {
            token = token.Token,
            expires = token.Expires
        });
        _logger.LogInformation("Payload appkey preparado com token JSON em camelCase e nascimento {BirthDate}.", journey.Subject.BirthDate.ToString("dd/MM/yyyy"));
        _logger.LogInformation(
            "Campos appkey enviados: user={User}; tokenKeys=token,expires; cpfLength={CpfLength}; nomeLength={NameLength}; nascimento={BirthDate}; idExternoCliente={ExternalClientId}.",
            credential.ProviderUser,
            JourneyValidator.DigitsOnly(journey.Subject.Cpf).Length,
            journey.Subject.FullName.Length,
            journey.Subject.BirthDate.ToString("dd/MM/yyyy"),
            journey.Subject.ExternalClientId
        );
        using var appkeyContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = credential.ProviderUser,
            ["token"] = serializedToken,
            ["cpf"] = JourneyValidator.DigitsOnly(journey.Subject.Cpf),
            ["nome"] = journey.Subject.FullName,
            ["nascimento"] = journey.Subject.BirthDate.ToString("dd/MM/yyyy"),
            ["idExternoCliente"] = journey.Subject.ExternalClientId
        });

        using var response = await _httpClient.PostAsync(
            $"{_options.BaseUrl}/facecaptcha/service/captcha/appkey",
            appkeyContent,
            cancellationToken
        );
        await EnsureSuccessOrThrowAsync(response, "appkey", cancellationToken);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("appkey", out var appkey))
        {
            return appkey.GetString() ?? throw new InvalidOperationException("Certiface retornou appkey vazia.");
        }

        if (document.RootElement.ValueKind == JsonValueKind.String)
        {
            return document.RootElement.GetString() ?? throw new InvalidOperationException("Certiface retornou appkey vazia.");
        }

        throw new InvalidOperationException("Resposta de appkey da Certiface não reconhecida.");
    }

    public async Task<JsonDocument> GetDocumentResultAsync(string appkey, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["appkey"] = appkey
        });

        using var response = await _httpClient.PostAsync(
            $"{_options.ResultBaseUrl}/facecaptcha/service/captcha/document/result",
            content,
            cancellationToken
        );
        await EnsureSuccessOrThrowAsync(response, "document/result", cancellationToken);

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    public async Task<CertifaceProxyResponse> Process3dRequestAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var providerPayload = CreateProcessRequestProviderPayload(payload);

        _logger.LogInformation(
            "Encaminhando process-request para Certiface somente com os campos appkey, requestBlob e userAgent."
        );

        for (var attempt = 0; attempt <= MaxProcessRequestTransientRetries; attempt++)
        {
            using var content = new StringContent(providerPayload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(
                $"{_options.BaseUrl}/facecaptcha/service/captcha/3d/process-request",
                content,
                cancellationToken
            );

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (!IsTransientServerError(statusCode) || attempt == MaxProcessRequestTransientRetries)
            {
                return new CertifaceProxyResponse(statusCode, body, response.Content.Headers.ContentType?.ToString() ?? "application/json");
            }

            _logger.LogWarning(
                "Certiface process-request retornou {StatusCode} na tentativa {Attempt}. Repetindo a chamada.",
                statusCode,
                attempt + 1
            );

            await Task.Delay(ProcessRequestRetryDelay, cancellationToken);
        }

        throw new InvalidOperationException("Fluxo inesperado ao processar process-request Certiface.");
    }

    public Task<CertifaceProxyResponse> InitializeV9Async(JsonElement payload, CancellationToken cancellationToken)
    {
        var providerPayload = JsonSerializer.Serialize(new
        {
            appkey = GetRequiredString(payload, "appkey"),
            platform = GetOptionalString(payload, "platform") ?? "web"
        });

        return PostJsonProxyAsync("/facecaptcha/service/captcha/3d/initialize", providerPayload, cancellationToken);
    }

    public Task<CertifaceProxyResponse> CreateV9SessionTokenAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var providerPayload = JsonSerializer.Serialize(new
        {
            appkey = GetRequiredString(payload, "appkey"),
            userAgent = GetRequiredString(payload, "userAgent")
        });

        return PostJsonProxyAsync("/facecaptcha/service/captcha/3d/session-token", providerPayload, cancellationToken);
    }

    public Task<CertifaceProxyResponse> ProcessV9LivenessAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var providerPayload = JsonSerializer.Serialize(new
        {
            appkey = GetRequiredString(payload, "appkey"),
            userAgent = GetRequiredString(payload, "userAgent"),
            faceScan = GetRequiredString(payload, "faceScan"),
            auditTrailImage = GetRequiredString(payload, "auditTrailImage"),
            lowQualityAuditTrailImage = GetRequiredString(payload, "lowQualityAuditTrailImage"),
            sessionId = GetRequiredString(payload, "sessionId")
        });

        return PostJsonProxyAsync("/facecaptcha/service/captcha/3d/liveness", providerPayload, cancellationToken);
    }

    private async Task<CertifaceProxyResponse> PostJsonProxyAsync(
        string path,
        string providerPayload,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(providerPayload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{_options.BaseUrl}{path}", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new CertifaceProxyResponse(
            (int)response.StatusCode,
            body,
            response.Content.Headers.ContentType?.ToString() ?? "application/json"
        );
    }

    private static string CreateProcessRequestProviderPayload(JsonElement payload)
    {
        return JsonSerializer.Serialize(new
        {
            appkey = GetRequiredString(payload, "appkey"),
            requestBlob = GetRequiredString(payload, "requestBlob"),
            userAgent = GetRequiredString(payload, "userAgent")
        });
    }

    private static string GetRequiredString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ArgumentException(
                $"O payload de process-request deve conter o campo textual obrigatório '{propertyName}'.",
                nameof(payload)
            );
        }

        return property.GetString()!;
    }

    private static string? GetOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task<CertifaceCredentialResponse> GetCredentialTokenAsync(TenantCredential credential, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = credential.ProviderUser,
            ["pass"] = credential.ProviderPass
        });

        using var response = await _httpClient.PostAsync(
            $"{_options.BaseUrl}/facecaptcha/service/captcha/credencial",
            content,
            cancellationToken
        );
        await EnsureSuccessOrThrowAsync(response, "credencial", cancellationToken);

        var token = await response.Content.ReadFromJsonAsync<CertifaceCredentialResponse>(cancellationToken);
        return token ?? throw new InvalidOperationException("Certiface não retornou token de credencial.");
    }

    private async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "Certiface operation {Operation} failed with status {StatusCode}. Body: {Body}",
            operation,
            (int)response.StatusCode,
            body
        );

        throw new CertifaceProviderException(operation, (int)response.StatusCode, body);
    }

    private static bool IsTransientServerError(int statusCode)
    {
        return statusCode >= 500 && statusCode < 600;
    }
}

public sealed record CertifaceProxyResponse(int StatusCode, string Body, string ContentType);

public sealed class CertifaceProviderException : Exception
{
    public CertifaceProviderException(string operation, int statusCode, string responseBody)
        : base($"Certiface operation '{operation}' failed with status {statusCode}.")
    {
        Operation = operation;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public string Operation { get; }
    public int StatusCode { get; }
    public string ResponseBody { get; }
}
