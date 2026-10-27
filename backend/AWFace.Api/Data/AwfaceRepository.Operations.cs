using System.Text.Json;
using AWFace.Api.Domain;
using Npgsql;
using NpgsqlTypes;

namespace AWFace.Api.Data;

/// <summary>
/// Persistência operacional introduzida na 1.1.0: usuários administradores,
/// fila de webhooks (outbox) e deduplicação das requisições do SDK.
/// </summary>
public sealed partial class AwfaceRepository
{
    private const int MaxStoredResponseBodyLength = 4000;

    // ---------------------------------------------------------------- Administradores

    public async Task<bool> HasActiveAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from awface_admin_user where active)";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool exists && exists;
    }

    public async Task<AdminUser?> GetAdminByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, email, password_hash, active
            from awface_admin_user
            where lower(email) = lower(@email)
            """;
        command.Parameters.AddWithValue("email", email.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AdminUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }

    public async Task CreateAdminAsync(string email, string passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_admin_user (email, password_hash, active, created_at, updated_at)
            values (@email, @password_hash, true, now(), now())
            on conflict (email) do nothing
            """;
        command.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("password_hash", passwordHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- Outbox de webhooks

    /// <summary>
    /// Enfileira o webhook do Tenant. <paramref name="eventKey"/> garante idempotência:
    /// o mesmo evento nunca gera duas entregas.
    /// </summary>
    public async Task<bool> EnqueueCallbackAsync(
        Guid journeyId,
        string eventKey,
        Guid eventId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_callback_outbox (
                id, journey_id, event_key, payload, status, attempts, next_attempt_at, created_at, updated_at
            )
            values (@id, @journey_id, @event_key, cast(@payload as jsonb), 'PENDING', 0, now(), now(), now())
            on conflict (event_key) do nothing
            """;
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("event_key", eventKey);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Reserva itens vencidos (FOR UPDATE SKIP LOCKED permite várias instâncias da API).</summary>
    public async Task<IReadOnlyList<CallbackOutboxItem>> ClaimDueCallbacksAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update awface_callback_outbox outbox
            set status = 'SENDING',
                attempts = outbox.attempts + 1,
                locked_until = now() + interval '2 minutes',
                updated_at = now()
            where outbox.id in (
                select id
                from awface_callback_outbox
                where (status = 'PENDING' and next_attempt_at <= now())
                   or (status = 'SENDING' and locked_until < now())
                order by next_attempt_at
                limit @batch_size
                for update skip locked
            )
            returning outbox.id, outbox.journey_id, outbox.payload::text, outbox.attempts
            """;
        command.Parameters.AddWithValue("batch_size", batchSize);

        var items = new List<CallbackOutboxItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            items.Add(new CallbackOutboxItem(id, reader.GetGuid(1), id, reader.GetString(2), reader.GetInt32(3)));
        }

        return items;
    }

    public async Task CompleteCallbackAttemptAsync(
        CallbackOutboxItem item,
        string targetUrl,
        int? responseStatus,
        string? responseBody,
        bool delivered,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        var truncatedBody = Truncate(responseBody);
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var deliveryCommand = connection.CreateCommand())
        {
            deliveryCommand.Transaction = transaction;
            deliveryCommand.CommandText = """
                insert into awface_callback_delivery (
                    journey_id, outbox_id, target_url, request_payload, response_status, response_body, attempt, delivered_at, created_at
                )
                values (
                    @journey_id, @outbox_id, @target_url, cast(@request_payload as jsonb), @response_status, @response_body,
                    @attempt, case when @delivered then now() else null end, now()
                )
                """;
            deliveryCommand.Parameters.AddWithValue("journey_id", item.JourneyId);
            deliveryCommand.Parameters.AddWithValue("outbox_id", item.Id);
            deliveryCommand.Parameters.AddWithValue("target_url", targetUrl);
            deliveryCommand.Parameters.AddWithValue("request_payload", NpgsqlDbType.Jsonb, item.PayloadJson);
            deliveryCommand.Parameters.AddWithValue("response_status", (object?)responseStatus ?? DBNull.Value);
            deliveryCommand.Parameters.AddWithValue("response_body", (object?)truncatedBody ?? DBNull.Value);
            deliveryCommand.Parameters.AddWithValue("attempt", item.Attempts);
            deliveryCommand.Parameters.AddWithValue("delivered", delivered);
            await deliveryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var outboxCommand = connection.CreateCommand())
        {
            outboxCommand.Transaction = transaction;
            outboxCommand.CommandText = """
                update awface_callback_outbox
                set status = @status,
                    last_response_status = @response_status,
                    last_error = @last_error,
                    next_attempt_at = coalesce(@next_attempt_at, next_attempt_at),
                    delivered_at = case when @delivered then now() else delivered_at end,
                    locked_until = null,
                    updated_at = now()
                where id = @id
                """;
            var status = delivered
                ? CallbackOutboxState.DELIVERED
                : nextAttemptAt is null ? CallbackOutboxState.FAILED : CallbackOutboxState.PENDING;
            outboxCommand.Parameters.AddWithValue("id", item.Id);
            outboxCommand.Parameters.AddWithValue("status", status.ToString());
            outboxCommand.Parameters.AddWithValue("response_status", (object?)responseStatus ?? DBNull.Value);
            outboxCommand.Parameters.AddWithValue("last_error", delivered ? DBNull.Value : (object?)truncatedBody ?? DBNull.Value);
            outboxCommand.Parameters.AddWithValue("next_attempt_at", (object?)nextAttemptAt ?? DBNull.Value);
            outboxCommand.Parameters.AddWithValue("delivered", delivered);
            await outboxCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Último webhook enfileirado para a jornada a partir de <paramref name="createdSince"/>.</summary>
    public async Task<CallbackOutboxStatus?> GetLatestCallbackOutboxAsync(
        Guid journeyId,
        DateTimeOffset? createdSince,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, status, attempts, last_response_status, created_at, delivered_at
            from awface_callback_outbox
            where journey_id = @journey_id
              and (@created_since is null or created_at >= @created_since)
            order by created_at desc
            limit 1
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("created_since", NpgsqlDbType.TimestampTz, (object?)createdSince ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CallbackOutboxStatus(
            reader.GetGuid(0),
            Enum.Parse<CallbackOutboxState>(reader.GetString(1)),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)
        );
    }

    // ---------------------------------------------------------------- Deduplicação de requisições do SDK

    /// <summary>
    /// Registra o início do processamento de uma requisição do SDK identificada por <paramref name="requestKey"/>.
    /// Retorna o estado atual quando a mesma requisição já foi recebida.
    /// </summary>
    public async Task<SdkRequestRecord?> TryBeginSdkRequestAsync(
        string requestKey,
        Guid journeyId,
        string appkey,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = """
                insert into awface_sdk_request (request_key, journey_id, appkey, operation, status, created_at, updated_at)
                values (@request_key, @journey_id, @appkey, @operation, 'PROCESSING', now(), now())
                on conflict (request_key) do update
                set status = 'PROCESSING',
                    response_status = null,
                    response_content_type = null,
                    response_body = null,
                    updated_at = now()
                where awface_sdk_request.status = 'PROCESSING'
                  and awface_sdk_request.updated_at < now() - interval '2 minutes'
                returning request_key
                """;
            insertCommand.Parameters.AddWithValue("request_key", requestKey);
            insertCommand.Parameters.AddWithValue("journey_id", journeyId);
            insertCommand.Parameters.AddWithValue("appkey", appkey);
            insertCommand.Parameters.AddWithValue("operation", operation);
            if (await insertCommand.ExecuteScalarAsync(cancellationToken) is string)
            {
                return null;
            }
        }

        return await GetSdkRequestAsync(connection, requestKey, cancellationToken);
    }

    public async Task<SdkRequestRecord?> GetSdkRequestAsync(string requestKey, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        return await GetSdkRequestAsync(connection, requestKey, cancellationToken);
    }

    public async Task CompleteSdkRequestAsync(
        string requestKey,
        int responseStatus,
        string responseContentType,
        string responseBody,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update awface_sdk_request
            set status = 'COMPLETED',
                response_status = @response_status,
                response_content_type = @response_content_type,
                response_body = @response_body,
                updated_at = now()
            where request_key = @request_key
            """;
        command.Parameters.AddWithValue("request_key", requestKey);
        command.Parameters.AddWithValue("response_status", responseStatus);
        command.Parameters.AddWithValue("response_content_type", responseContentType);
        command.Parameters.AddWithValue("response_body", responseBody);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Libera a chave para que uma nova tentativa do SDK seja processada (falha não cacheada).</summary>
    public async Task ReleaseSdkRequestAsync(string requestKey, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from awface_sdk_request where request_key = @request_key and status = 'PROCESSING'";
        command.Parameters.AddWithValue("request_key", requestKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeSdkRequestsAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            delete from awface_sdk_request
            where updated_at < now() - (@minutes * interval '1 minute')
            """;
        command.Parameters.AddWithValue("minutes", Math.Max(1, (int)olderThan.TotalMinutes));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SdkRequestRecord?> GetSdkRequestAsync(NpgsqlConnection connection, string requestKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select status, response_status, response_content_type, response_body
            from awface_sdk_request
            where request_key = @request_key
            """;
        command.Parameters.AddWithValue("request_key", requestKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SdkRequestRecord(
            reader.GetString(0) == "COMPLETED",
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)
        );
    }

    // ---------------------------------------------------------------- Histórico de tentativas

    public async Task RegisterAttemptEventAsync(
        Guid journeyId,
        string appkey,
        LivenessEngine livenessEngine,
        string eventType,
        string providerResponseJson,
        string? deviceLocationJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_attempt_history (
                journey_id, appkey, liveness_engine, event_type, provider_response, device_location, created_at
            )
            values (
                @journey_id, @appkey, @liveness_engine, @event_type,
                cast(@provider_response as jsonb), cast(@device_location as jsonb), now()
            )
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("appkey", appkey);
        command.Parameters.AddWithValue("liveness_engine", livenessEngine.ToString());
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("provider_response", NpgsqlDbType.Jsonb, providerResponseJson);
        command.Parameters.AddWithValue(
            "device_location",
            NpgsqlDbType.Jsonb,
            string.IsNullOrWhiteSpace(deviceLocationJson) ? "null" : deviceLocationJson
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string SerializePayload(object payload)
    {
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= MaxStoredResponseBodyLength)
        {
            return value;
        }

        return value[..MaxStoredResponseBodyLength] + "…";
    }
}

public sealed record SdkRequestRecord(
    bool Completed,
    int? ResponseStatus,
    string? ResponseContentType,
    string? ResponseBody
);
