using System.Text.Json.Nodes;
using AWFace.Api.Configuration;
using AWFace.Api.Data;
using AWFace.Api.Domain;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Services;

/// <summary>Sinal em memória para acordar o <see cref="CallbackDispatcher"/> assim que um webhook é enfileirado.</summary>
public sealed class CallbackOutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(timeout, cancellationToken);
    }
}

/// <summary>Enfileira webhooks do Tenant de forma idempotente.</summary>
public sealed class CallbackOutbox
{
    private readonly AwfaceRepository _repository;
    private readonly CallbackOutboxSignal _signal;

    public CallbackOutbox(AwfaceRepository repository, CallbackOutboxSignal signal)
    {
        _repository = repository;
        _signal = signal;
    }

    public async Task<bool> EnqueueAsync(Guid journeyId, string eventKey, JsonObject payload, CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        payload["eventId"] = eventId.ToString();
        var enqueued = await _repository.EnqueueCallbackAsync(
            journeyId,
            eventKey,
            eventId,
            AwfaceRepository.SerializePayload(payload),
            cancellationToken
        );

        if (enqueued)
        {
            _signal.Notify();
        }

        return enqueued;
    }
}

/// <summary>
/// Entrega os webhooks do Tenant com retentativa exponencial, desacoplado da requisição do SDK.
/// Cada tentativa é registrada em awface_callback_delivery.
/// </summary>
public sealed class CallbackDispatcher : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
    ];

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SdkRequestRetention = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CallbackOutboxSignal _signal;
    private readonly TenantWebhookClient _webhookClient;
    private readonly WebhookOptions _options;
    private readonly ILogger<CallbackDispatcher> _logger;
    private DateTimeOffset _nextHousekeepingAt = DateTimeOffset.MinValue;

    public CallbackDispatcher(
        IServiceScopeFactory scopeFactory,
        CallbackOutboxSignal signal,
        TenantWebhookClient webhookClient,
        IOptions<AwfaceOptions> options,
        ILogger<CallbackDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _webhookClient = webhookClient;
        _options = options.Value.Webhook;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await DispatchDueAsync(stoppingToken);
                await HousekeepingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Falha no processamento da fila de webhooks do Tenant.");
            }

            if (processed == 0)
            {
                try
                {
                    await _signal.WaitAsync(IdlePollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<AwfaceRepository>();
        var items = await repository.ClaimDueCallbacksAsync(10, cancellationToken);

        foreach (var item in items)
        {
            await DeliverAsync(repository, item, cancellationToken);
        }

        return items.Count;
    }

    private async Task DeliverAsync(AwfaceRepository repository, CallbackOutboxItem item, CancellationToken cancellationToken)
    {
        var journey = await repository.GetJourneyByIdAsync(item.JourneyId, cancellationToken);
        if (journey is null)
        {
            await repository.CompleteCallbackAttemptAsync(item, "", null, "Jornada não encontrada.", false, null, cancellationToken);
            return;
        }

        var result = await _webhookClient.SendAsync(journey.Tenant, item.EventId, item.PayloadJson, cancellationToken);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        DateTimeOffset? nextAttemptAt = null;
        if (!result.Delivered && item.Attempts < maxAttempts)
        {
            nextAttemptAt = DateTimeOffset.UtcNow.Add(Backoff[Math.Min(item.Attempts - 1, Backoff.Length - 1)]);
        }

        await repository.CompleteCallbackAttemptAsync(
            item,
            journey.Tenant.CallbackUrl,
            result.StatusCode,
            result.ResponseBody,
            result.Delivered,
            nextAttemptAt,
            cancellationToken
        );

        if (result.Delivered)
        {
            _logger.LogInformation(
                "Webhook do Tenant entregue. JourneyId={JourneyId}, EventId={EventId}, tentativa={Attempt}, status={Status}.",
                item.JourneyId,
                item.EventId,
                item.Attempts,
                result.StatusCode
            );
        }
        else if (nextAttemptAt is not null)
        {
            _logger.LogWarning(
                "Webhook do Tenant não entregue. JourneyId={JourneyId}, EventId={EventId}, tentativa={Attempt}/{MaxAttempts}, status={Status}. Nova tentativa em {NextAttemptAt:O}.",
                item.JourneyId,
                item.EventId,
                item.Attempts,
                maxAttempts,
                result.StatusCode,
                nextAttemptAt
            );
        }
        else
        {
            _logger.LogError(
                "Webhook do Tenant descartado após {Attempts} tentativas. JourneyId={JourneyId}, EventId={EventId}, último status={Status}.",
                item.Attempts,
                item.JourneyId,
                item.EventId,
                result.StatusCode
            );
        }
    }

    private async Task HousekeepingAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextHousekeepingAt)
        {
            return;
        }

        _nextHousekeepingAt = DateTimeOffset.UtcNow.AddHours(1);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<AwfaceRepository>();
        var removed = await repository.PurgeSdkRequestsAsync(SdkRequestRetention, cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("Cache de deduplicação do SDK: {Removed} registros expirados removidos.", removed);
        }
    }
}
