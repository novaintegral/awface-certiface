using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AWFace.Api.Domain;

namespace AWFace.Api.Services;

public sealed class TenantWebhookClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantWebhookClient> _logger;
    private readonly ConcurrentDictionary<string, CachedAccessToken> _tokenCache = new();

    public TenantWebhookClient(IHttpClientFactory httpClientFactory, ILogger<TenantWebhookClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TenantWebhookResult> SendAsync(Tenant tenant, object payload, CancellationToken cancellationToken)
    {
        using var callbackClient = _httpClientFactory.CreateClient("TenantCallback");
        using var request = new HttpRequestMessage(HttpMethod.Post, tenant.CallbackUrl)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        request.Headers.Remove("X-AWFace-SecureCallback");
        request.Headers.Add("X-AWFace-SecureCallback", tenant.SecureCallbackToken);

        if (tenant.CallbackOAuthEnabled)
        {
            var accessToken = await GetAccessTokenAsync(tenant, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await callbackClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new TenantWebhookResult(response.IsSuccessStatusCode, (int)response.StatusCode, responseBody);
    }

    private async Task<string> GetAccessTokenAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenant.CallbackOAuthTokenUrl)
            || string.IsNullOrWhiteSpace(tenant.CallbackOAuthClientId)
            || string.IsNullOrWhiteSpace(tenant.CallbackOAuthClientSecret))
        {
            throw new InvalidOperationException("OAuth2 do webhook está habilitado, mas Token URL, Client ID ou Client Secret não foram configurados.");
        }

        var cacheKey = $"{tenant.Id:N}:{tenant.CallbackOAuthTokenUrl}:{tenant.CallbackOAuthClientId}";
        if (_tokenCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return cached.AccessToken;
        }

        using var tokenClient = _httpClientFactory.CreateClient("TenantCallback");
        using var request = new HttpRequestMessage(HttpMethod.Post, tenant.CallbackOAuthTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = tenant.CallbackOAuthClientId,
                ["client_secret"] = tenant.CallbackOAuthClientSecret
            })
        };

        using var response = await tokenClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Falha ao obter access_token OAuth2 do webhook para TenantId {TenantId}. Status={Status}. Body={Body}",
                tenant.Id,
                (int)response.StatusCode,
                body
            );
            throw new InvalidOperationException($"Falha ao obter access_token OAuth2 do webhook. Status={(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException("Resposta OAuth2 não contém access_token.");
        }

        var accessToken = tokenElement.GetString()!;
        if (TryGetExpiresIn(root, out var expiresIn) && expiresIn > 0)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, expiresIn - 60));
            _tokenCache[cacheKey] = new CachedAccessToken(accessToken, expiresAt);
            _logger.LogInformation("Access_token OAuth2 do webhook armazenado em cache para TenantId {TenantId} até {ExpiresAt}.", tenant.Id, expiresAt);
        }

        return accessToken;
    }

    private static bool TryGetExpiresIn(JsonElement root, out int expiresIn)
    {
        expiresIn = 0;
        if (!root.TryGetProperty("expires_in", out var expiresElement))
        {
            return false;
        }

        return expiresElement.ValueKind switch
        {
            JsonValueKind.Number => expiresElement.TryGetInt32(out expiresIn),
            JsonValueKind.String => int.TryParse(expiresElement.GetString(), out expiresIn),
            _ => false
        };
    }

    private sealed record CachedAccessToken(string AccessToken, DateTimeOffset ExpiresAt);
}

public sealed record TenantWebhookResult(bool Delivered, int StatusCode, string ResponseBody);
