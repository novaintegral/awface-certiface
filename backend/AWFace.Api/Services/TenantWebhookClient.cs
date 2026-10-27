using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AWFace.Api.Configuration;
using AWFace.Api.Domain;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Services;

public sealed class TenantWebhookClient
{
    public const string SignatureHeader = "X-AWFace-Signature";
    public const string EventIdHeader = "X-AWFace-Event-Id";
    public const string TimestampHeader = "X-AWFace-Timestamp";
    public const string LegacySecureCallbackHeader = "X-AWFace-SecureCallback";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantWebhookClient> _logger;
    private readonly WebhookOptions _options;
    private readonly ConcurrentDictionary<string, CachedAccessToken> _tokenCache = new();

    public TenantWebhookClient(IHttpClientFactory httpClientFactory, ILogger<TenantWebhookClient> logger, IOptions<AwfaceOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value.Webhook;
    }

    /// <summary>
    /// Envia o webhook assinado. A assinatura é <c>t={unix},v1={hex(HMAC-SHA256(secureCallbackToken, "{t}.{corpo}"))}</c>.
    /// </summary>
    public async Task<TenantWebhookResult> SendAsync(Tenant tenant, Guid eventId, string payloadJson, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 2, 60)));
        var effectiveToken = timeoutCts.Token;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes(payloadJson);

        using var callbackClient = _httpClientFactory.CreateClient("TenantCallback");
        using var request = new HttpRequestMessage(HttpMethod.Post, tenant.CallbackUrl)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        request.Headers.Add(LegacySecureCallbackHeader, tenant.SecureCallbackToken);
        request.Headers.Add(EventIdHeader, eventId.ToString());
        request.Headers.Add(TimestampHeader, timestamp);
        request.Headers.Add(SignatureHeader, $"t={timestamp},v1={ComputeSignature(tenant.SecureCallbackToken, timestamp, body)}");

        try
        {
            if (tenant.CallbackOAuthEnabled)
            {
                var accessToken = await GetAccessTokenAsync(tenant, effectiveToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            using var response = await callbackClient.SendAsync(request, effectiveToken);
            var responseBody = await response.Content.ReadAsStringAsync(effectiveToken);
            return new TenantWebhookResult(response.IsSuccessStatusCode, (int)response.StatusCode, responseBody);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TenantWebhookResult(false, 408, "Timeout ao comunicar o webhook do Tenant.");
        }
        catch (HttpRequestException exception)
        {
            return new TenantWebhookResult(false, null, $"Falha de rede ao comunicar o webhook do Tenant: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return new TenantWebhookResult(false, null, exception.Message);
        }
    }

    public static string ComputeSignature(string secret, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, signed, prefix.Length, body.Length);
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)).ToLowerInvariant();
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
                "Falha ao obter access_token OAuth2 do webhook para TenantId {TenantId}. Status={Status}.",
                tenant.Id,
                (int)response.StatusCode
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

public sealed record TenantWebhookResult(bool Delivered, int? StatusCode, string ResponseBody);
