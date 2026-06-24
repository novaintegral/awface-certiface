using System.Text.Json;
using AWFace.Api.Contracts;
using AWFace.Api.Domain;
using AWFace.Api.Security;
using AWFace.Api.Services;
using Npgsql;
using NpgsqlTypes;

namespace AWFace.Api.Data;

public sealed class AwfaceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AwfaceDb _db;
    private readonly SensitiveDataProtector _protector;

    public AwfaceRepository(AwfaceDb db, SensitiveDataProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        var tenants = new List<Tenant>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, integration_token, status::text, terms_url, privacy_url, logo_base64,
                   theme, primary_color, secondary_color, callback_url, secure_callback_token,
                   callback_oauth_enabled, callback_oauth_token_url, callback_oauth_client_id, callback_oauth_client_secret_ciphertext,
                   created_at, updated_at
            from awface_tenant
            order by created_at desc
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(ReadTenant(reader, []));
        }

        await reader.CloseAsync();

        for (var i = 0; i < tenants.Count; i++)
        {
            tenants[i] = tenants[i] with { Credentials = await ListCredentialsAsync(connection, tenants[i].Id, cancellationToken) };
        }

        return tenants;
    }

    public async Task<Tenant?> GetTenantByTokenAsync(string integrationToken, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        return await GetTenantByTokenAsync(connection, integrationToken, cancellationToken);
    }

    public async Task<Tenant?> GetTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        return await GetTenantByIdAsync(connection, tenantId, cancellationToken);
    }

    public async Task<Tenant> UpsertTenantAsync(TenantUpsertRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var tenantId = request.Id.GetValueOrDefault(Guid.NewGuid());
        var token = string.IsNullOrWhiteSpace(request.IntegrationToken)
            ? $"awf_{Guid.NewGuid():N}"
            : request.IntegrationToken.Trim();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into awface_tenant (
                    id, name, integration_token, status, terms_url, privacy_url, logo_base64,
                    theme, primary_color, secondary_color, callback_url, secure_callback_token,
                    callback_oauth_enabled, callback_oauth_token_url, callback_oauth_client_id, callback_oauth_client_secret_ciphertext,
                    created_at, updated_at
                )
                values (
                    @id, @name, @integration_token, cast(@status as tenant_status), @terms_url, @privacy_url, @logo_base64,
                    @theme, @primary_color, @secondary_color, @callback_url, @secure_callback_token,
                    @callback_oauth_enabled, @callback_oauth_token_url, @callback_oauth_client_id, @callback_oauth_client_secret_ciphertext,
                    now(), now()
                )
                on conflict (id) do update set
                    name = excluded.name,
                    integration_token = excluded.integration_token,
                    status = excluded.status,
                    terms_url = excluded.terms_url,
                    privacy_url = excluded.privacy_url,
                    logo_base64 = excluded.logo_base64,
                    theme = excluded.theme,
                    primary_color = excluded.primary_color,
                    secondary_color = excluded.secondary_color,
                    callback_url = excluded.callback_url,
                    secure_callback_token = excluded.secure_callback_token,
                    callback_oauth_enabled = excluded.callback_oauth_enabled,
                    callback_oauth_token_url = excluded.callback_oauth_token_url,
                    callback_oauth_client_id = excluded.callback_oauth_client_id,
                    callback_oauth_client_secret_ciphertext = excluded.callback_oauth_client_secret_ciphertext,
                    updated_at = now()
                """;
            command.Parameters.AddWithValue("id", tenantId);
            command.Parameters.AddWithValue("name", request.Name.Trim());
            command.Parameters.AddWithValue("integration_token", token);
            command.Parameters.AddWithValue("status", request.Status.ToString());
            command.Parameters.AddWithValue("terms_url", request.TermsUrl.Trim());
            command.Parameters.AddWithValue("privacy_url", request.PrivacyUrl.Trim());
            command.Parameters.AddWithValue("logo_base64", (object?)request.LogoBase64 ?? DBNull.Value);
            command.Parameters.AddWithValue("theme", NormalizeTheme(request.Theme));
            command.Parameters.AddWithValue("primary_color", NormalizeColor(request.PrimaryColor, "#007060"));
            command.Parameters.AddWithValue("secondary_color", NormalizeColor(request.SecondaryColor, "#315f88"));
            command.Parameters.AddWithValue("callback_url", request.CallbackUrl.Trim());
            command.Parameters.AddWithValue("secure_callback_token", request.SecureCallbackToken.Trim());
            command.Parameters.AddWithValue("callback_oauth_enabled", request.CallbackOAuthEnabled);
            command.Parameters.AddWithValue("callback_oauth_token_url", (object?)NormalizeOptional(request.CallbackOAuthTokenUrl) ?? DBNull.Value);
            command.Parameters.AddWithValue("callback_oauth_client_id", (object?)NormalizeOptional(request.CallbackOAuthClientId) ?? DBNull.Value);
            command.Parameters.AddWithValue("callback_oauth_client_secret_ciphertext", ProtectOptional(request.CallbackOAuthClientSecret));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCredentials = connection.CreateCommand())
        {
            deleteCredentials.Transaction = transaction;
            deleteCredentials.CommandText = "delete from awface_tenant_liveness_credential where tenant_id = @tenant_id";
            deleteCredentials.Parameters.AddWithValue("tenant_id", tenantId);
            await deleteCredentials.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var credential in request.Credentials)
        {
            await using var credentialCommand = connection.CreateCommand();
            credentialCommand.Transaction = transaction;
            credentialCommand.CommandText = """
                insert into awface_tenant_liveness_credential (
                    tenant_id, journey_type, provider_user, provider_pass_ciphertext, created_at, updated_at
                )
                values (@tenant_id, cast(@journey_type as journey_type), @provider_user, @provider_pass_ciphertext, now(), now())
                """;
            credentialCommand.Parameters.AddWithValue("tenant_id", tenantId);
            credentialCommand.Parameters.AddWithValue("journey_type", credential.JourneyType.ToString());
            credentialCommand.Parameters.AddWithValue("provider_user", credential.ProviderUser.Trim());
            credentialCommand.Parameters.AddWithValue("provider_pass_ciphertext", _protector.Protect(credential.ProviderPass));
            await credentialCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetTenantByIdAsync(connection, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant salvo, mas não encontrado.");
    }

    public async Task<Tenant?> UpdateTenantStatusAsync(Guid tenantId, TenantStatus status, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update awface_tenant
            set status = cast(@status as tenant_status),
                cancelled_at = case when @status = 'CANCELLED' then now() else cancelled_at end,
                updated_at = now()
            where id = @id
            """;
        command.Parameters.AddWithValue("id", tenantId);
        command.Parameters.AddWithValue("status", status.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetTenantByIdAsync(connection, tenantId, cancellationToken);
    }

    public async Task<JourneySession?> GetJourneyByIdAsync(Guid journeyId, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken);
    }

    public async Task<JourneySession?> GetJourneyByAppkeyAsync(string appkey, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = JourneySelectSql + """
             where j.appkey = @appkey
                or exists (
                    select 1
                    from awface_liveness_appkey_history history
                    where history.journey_id = j.id
                      and history.appkey = @appkey
                )
            """;
        command.Parameters.AddWithValue("appkey", appkey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var journey = ReadJourney(reader, []);
        await reader.CloseAsync();
        return journey with
        {
            Tenant = journey.Tenant with
            {
                Credentials = await ListCredentialsAsync(connection, journey.Tenant.Id, cancellationToken)
            }
        };
    }

    public async Task<JourneySession> CreateJourneyAsync(
        Tenant tenant,
        JourneyStartRequest request,
        LivenessEngine livenessEngine,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        var journeyId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_journey (
                id, tenant_id, journey_type, liveness_engine, cpf_hash, cpf_ciphertext, full_name_ciphertext,
                birth_date_ciphertext, external_client_id, status, user_agent, created_at, updated_at
            )
            values (
                @id, @tenant_id, cast(@journey_type as journey_type), @liveness_engine, @cpf_hash, @cpf_ciphertext, @full_name_ciphertext,
                @birth_date_ciphertext, @external_client_id, 'CREATED', @user_agent, now(), now()
            )
            """;
        command.Parameters.AddWithValue("id", journeyId);
        command.Parameters.AddWithValue("tenant_id", tenant.Id);
        command.Parameters.AddWithValue("journey_type", request.JourneyType.ToString());
        command.Parameters.AddWithValue("liveness_engine", livenessEngine.ToString());
        command.Parameters.AddWithValue("cpf_hash", _protector.Hash(JourneyValidator.DigitsOnly(request.Cpf)));
        command.Parameters.AddWithValue("cpf_ciphertext", _protector.Protect(JourneyValidator.DigitsOnly(request.Cpf)));
        command.Parameters.AddWithValue("full_name_ciphertext", _protector.Protect(request.FullName.Trim()));
        command.Parameters.AddWithValue("birth_date_ciphertext", _protector.Protect(request.BirthDate.ToString("yyyy-MM-dd")));
        command.Parameters.AddWithValue("external_client_id", request.ExternalClientId.Trim());
        command.Parameters.AddWithValue("user_agent", (object?)userAgent ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken)
            ?? throw new InvalidOperationException("Jornada criada, mas não encontrada.");
    }

    public async Task<JourneySession> CreateJourneyLaunchAsync(
        Tenant tenant,
        JourneyLaunchRequest request,
        string launchTokenHash,
        DateTimeOffset launchExpiresAt,
        LivenessEngine livenessEngine,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        var journeyId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_journey (
                id, tenant_id, journey_type, liveness_engine, cpf_hash, cpf_ciphertext, full_name_ciphertext,
                birth_date_ciphertext, external_client_id, status, user_agent,
                launch_token_hash, launch_expires_at, host_reference, host_metadata,
                created_at, updated_at
            )
            values (
                @id, @tenant_id, cast(@journey_type as journey_type), @liveness_engine, @cpf_hash, @cpf_ciphertext, @full_name_ciphertext,
                @birth_date_ciphertext, @external_client_id, 'CREATED', @user_agent,
                @launch_token_hash, @launch_expires_at, @host_reference, cast(@host_metadata as jsonb),
                now(), now()
            )
            """;
        command.Parameters.AddWithValue("id", journeyId);
        command.Parameters.AddWithValue("tenant_id", tenant.Id);
        command.Parameters.AddWithValue("journey_type", request.JourneyType.ToString());
        command.Parameters.AddWithValue("liveness_engine", livenessEngine.ToString());
        command.Parameters.AddWithValue("cpf_hash", _protector.Hash(JourneyValidator.DigitsOnly(request.Cpf)));
        command.Parameters.AddWithValue("cpf_ciphertext", _protector.Protect(JourneyValidator.DigitsOnly(request.Cpf)));
        command.Parameters.AddWithValue("full_name_ciphertext", _protector.Protect(request.FullName.Trim()));
        command.Parameters.AddWithValue("birth_date_ciphertext", _protector.Protect(request.BirthDate.ToString("yyyy-MM-dd")));
        command.Parameters.AddWithValue("external_client_id", request.ExternalClientId.Trim());
        command.Parameters.AddWithValue("user_agent", (object?)userAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("launch_token_hash", launchTokenHash);
        command.Parameters.AddWithValue("launch_expires_at", launchExpiresAt);
        command.Parameters.AddWithValue("host_reference", (object?)request.HostReference?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("host_metadata", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(request.Metadata ?? new { }, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken)
            ?? throw new InvalidOperationException("Jornada autonoma criada, mas nao encontrada.");
    }

    public async Task<JourneySession?> ConsumeJourneyLaunchAsync(string launchTokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid? journeyId = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                update awface_liveness_journey
                set launch_consumed_at = coalesce(launch_consumed_at, now()),
                    updated_at = now()
                where launch_token_hash = @launch_token_hash
                  and launch_expires_at > now()
                returning id
                """;
            command.Parameters.AddWithValue("launch_token_hash", launchTokenHash);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                journeyId = reader.GetGuid(0);
            }
        }

        if (journeyId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetJourneyByIdAsync(connection, journeyId.Value, cancellationToken);
    }

    public async Task<JourneySession?> RegisterConsentAsync(Guid journeyId, ConsentDecision decision, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var consentCommand = connection.CreateCommand())
        {
            consentCommand.Transaction = transaction;
            consentCommand.CommandText = """
                insert into awface_liveness_consent (journey_id, decision, decided_at, ip_address, user_agent)
                values (@journey_id, cast(@decision as consent_decision), now(), cast(@ip_address as inet), @user_agent)
                """;
            consentCommand.Parameters.AddWithValue("journey_id", journeyId);
            consentCommand.Parameters.AddWithValue("decision", decision.ToString());
            consentCommand.Parameters.AddWithValue("ip_address", (object?)ipAddress ?? DBNull.Value);
            consentCommand.Parameters.AddWithValue("user_agent", (object?)userAgent ?? DBNull.Value);
            await consentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journeyCommand = connection.CreateCommand())
        {
            journeyCommand.Transaction = transaction;
            journeyCommand.CommandText = """
                update awface_liveness_journey
                set status = cast(@status as journey_status),
                    updated_at = now()
                where id = @journey_id
                """;
            journeyCommand.Parameters.AddWithValue("journey_id", journeyId);
            journeyCommand.Parameters.AddWithValue("status", decision == ConsentDecision.ACCEPTED ? JourneyStatus.CONSENT_ACCEPTED.ToString() : JourneyStatus.CONSENT_REFUSED.ToString());
            await journeyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken);
    }

    public async Task<JourneySession?> SetJourneyAppkeyAsync(Guid journeyId, string appkey, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockJourneyCommand = connection.CreateCommand())
        {
            lockJourneyCommand.Transaction = transaction;
            lockJourneyCommand.CommandText = """
                select id
                from awface_liveness_journey
                where id = @journey_id
                for update
                """;
            lockJourneyCommand.Parameters.AddWithValue("journey_id", journeyId);
            var lockedJourneyId = await lockJourneyCommand.ExecuteScalarAsync(cancellationToken);
            if (lockedJourneyId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var closePreviousCommand = connection.CreateCommand())
        {
            closePreviousCommand.Transaction = transaction;
            closePreviousCommand.CommandText = """
                update awface_liveness_appkey_history
                set superseded_at = now()
                where journey_id = @journey_id
                  and superseded_at is null
                """;
            closePreviousCommand.Parameters.AddWithValue("journey_id", journeyId);
            await closePreviousCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.Transaction = transaction;
            historyCommand.CommandText = """
                insert into awface_liveness_appkey_history (
                    journey_id, appkey, liveness_engine, attempt, issued_at
                )
                select
                    id,
                    @appkey,
                    liveness_engine,
                    coalesce((
                        select max(history.attempt)
                        from awface_liveness_appkey_history history
                        where history.journey_id = awface_liveness_journey.id
                    ), 0) + 1,
                    now()
                from awface_liveness_journey
                where id = @journey_id
                """;
            historyCommand.Parameters.AddWithValue("journey_id", journeyId);
            historyCommand.Parameters.AddWithValue("appkey", appkey);
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journeyCommand = connection.CreateCommand())
        {
            journeyCommand.Transaction = transaction;
            journeyCommand.CommandText = """
                update awface_liveness_journey
                set appkey = @appkey,
                    appkey_created_at = now(),
                    status = 'APPKEY_CREATED',
                    updated_at = now()
                where id = @journey_id
                """;
            journeyCommand.Parameters.AddWithValue("journey_id", journeyId);
            journeyCommand.Parameters.AddWithValue("appkey", appkey);
            await journeyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken);
    }

    public async Task<string?> GetReusableAppkeyAsync(Guid journeyId, TimeSpan appkeyLifetime, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select appkey
            from awface_liveness_journey
            where id = @journey_id
              and appkey is not null
              and appkey_created_at is not null
              and appkey_created_at > now() - (@appkey_lifetime_minutes * interval '1 minute')
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("appkey_lifetime_minutes", Math.Max(1, (int)Math.Ceiling(appkeyLifetime.TotalMinutes)));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string appkey ? appkey : null;
    }

    public async Task MarkJourneyCompletedAsync(Guid journeyId, JsonDocument result, string? deviceLocationJson, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var location = ParseDeviceLocation(deviceLocationJson);

        await using (var resultCommand = connection.CreateCommand())
        {
            resultCommand.Transaction = transaction;
            resultCommand.CommandText = """
                insert into awface_liveness_result (
                    journey_id, provider_status, id_externo_cliente, bureau_payload,
                    liveness_payload,
                    location_latitude, location_longitude, location_accuracy, location_captured_at,
                    created_at
                )
                values (
                    @journey_id, @provider_status, @id_externo_cliente, cast(@bureau_payload as jsonb),
                    cast(@liveness_payload as jsonb),
                    @location_latitude, @location_longitude, @location_accuracy, @location_captured_at,
                    now()
                )
                """;
            var root = result.RootElement;
            resultCommand.Parameters.AddWithValue("journey_id", journeyId);
            resultCommand.Parameters.AddWithValue("provider_status", root.TryGetProperty("status", out var status) ? status.GetString() ?? "Completo" : "Completo");
            resultCommand.Parameters.AddWithValue("id_externo_cliente", root.TryGetProperty("idExternoCliente", out var externalId) ? externalId.GetString() ?? (object)DBNull.Value : DBNull.Value);
            AddJsonParameter(resultCommand, "bureau_payload", root, "certifaceID");
            AddJsonParameter(resultCommand, "liveness_payload", root, "facecaptcha");
            resultCommand.Parameters.AddWithValue("location_latitude", (object?)location?.Latitude ?? DBNull.Value);
            resultCommand.Parameters.AddWithValue("location_longitude", (object?)location?.Longitude ?? DBNull.Value);
            resultCommand.Parameters.AddWithValue("location_accuracy", (object?)location?.Accuracy ?? DBNull.Value);
            resultCommand.Parameters.AddWithValue("location_captured_at", (object?)location?.CapturedAt ?? DBNull.Value);
            await resultCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journeyCommand = connection.CreateCommand())
        {
            journeyCommand.Transaction = transaction;
            journeyCommand.CommandText = """
                update awface_liveness_journey
                set status = 'COMPLETED',
                    completed_at = now(),
                    updated_at = now()
                where id = @journey_id
                """;
            journeyCommand.Parameters.AddWithValue("journey_id", journeyId);
            await journeyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertJourneyFaceAssetAsync(
        Guid journeyId,
        Guid tenantId,
        string assetType,
        StoredFaceAsset asset,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_face_asset (
                journey_id, tenant_id, asset_type, storage_key, content_type, sha256, size_bytes, encryption_algorithm, created_at
            )
            values (
                @journey_id, @tenant_id, @asset_type, @storage_key, @content_type, @sha256, @size_bytes, @encryption_algorithm, now()
            )
            on conflict (journey_id, asset_type) do update
            set storage_key = excluded.storage_key,
                content_type = excluded.content_type,
                sha256 = excluded.sha256,
                size_bytes = excluded.size_bytes,
                encryption_algorithm = excluded.encryption_algorithm,
                created_at = now()
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("asset_type", assetType);
        command.Parameters.AddWithValue("storage_key", asset.StorageKey);
        command.Parameters.AddWithValue("content_type", asset.ContentType);
        command.Parameters.AddWithValue("sha256", asset.Sha256);
        command.Parameters.AddWithValue("size_bytes", asset.SizeBytes);
        command.Parameters.AddWithValue("encryption_algorithm", asset.EncryptionAlgorithm);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JourneyFaceAsset?> GetJourneyFaceAssetAsync(Guid journeyId, string assetType, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select journey_id, asset_type, storage_key, content_type, sha256, size_bytes, encryption_algorithm, created_at
            from awface_liveness_face_asset
            where journey_id = @journey_id
              and asset_type = @asset_type
            limit 1
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("asset_type", assetType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JourneyFaceAsset(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7)
        );
    }

    public async Task RegisterCallbackDeliveryAsync(Guid journeyId, string targetUrl, object payload, int? responseStatus, string? responseBody, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_callback_delivery (
                journey_id, target_url, request_payload, response_status, response_body, attempt, delivered_at, created_at
            )
            values (@journey_id, @target_url, cast(@request_payload as jsonb), @response_status, @response_body, 1, now(), now())
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("target_url", targetUrl);
        command.Parameters.AddWithValue("request_payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(payload, JsonOptions));
        command.Parameters.AddWithValue("response_status", (object?)responseStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("response_body", (object?)responseBody ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CallbackDeliveryStatus?> GetLatestCallbackDeliveryAsync(Guid journeyId, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select response_status, response_body, delivered_at
            from awface_callback_delivery
            where journey_id = @journey_id
            order by created_at desc
            limit 1
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CallbackDeliveryStatus(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2)
        );
    }

    private async Task<Tenant?> GetTenantByTokenAsync(NpgsqlConnection connection, string integrationToken, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, integration_token, status::text, terms_url, privacy_url, logo_base64,
                   theme, primary_color, secondary_color, callback_url, secure_callback_token,
                   callback_oauth_enabled, callback_oauth_token_url, callback_oauth_client_id, callback_oauth_client_secret_ciphertext,
                   created_at, updated_at
            from awface_tenant
            where integration_token = @integration_token
            """;
        command.Parameters.AddWithValue("integration_token", integrationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var tenant = ReadTenant(reader, []);
        await reader.CloseAsync();
        return tenant with { Credentials = await ListCredentialsAsync(connection, tenant.Id, cancellationToken) };
    }

    private async Task<Tenant?> GetTenantByIdAsync(NpgsqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, integration_token, status::text, terms_url, privacy_url, logo_base64,
                   theme, primary_color, secondary_color, callback_url, secure_callback_token,
                   callback_oauth_enabled, callback_oauth_token_url, callback_oauth_client_id, callback_oauth_client_secret_ciphertext,
                   created_at, updated_at
            from awface_tenant
            where id = @id
            """;
        command.Parameters.AddWithValue("id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var tenant = ReadTenant(reader, []);
        await reader.CloseAsync();
        return tenant with { Credentials = await ListCredentialsAsync(connection, tenant.Id, cancellationToken) };
    }

    private async Task<JourneySession?> GetJourneyByIdAsync(NpgsqlConnection connection, Guid journeyId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = JourneySelectSql + " where j.id = @id";
        command.Parameters.AddWithValue("id", journeyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var journey = ReadJourney(reader, []);
        await reader.CloseAsync();
        return journey with
        {
            Tenant = journey.Tenant with
            {
                Credentials = await ListCredentialsAsync(connection, journey.Tenant.Id, cancellationToken)
            }
        };
    }

    private async Task<IReadOnlyList<TenantCredential>> ListCredentialsAsync(NpgsqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, journey_type::text, provider_user, provider_pass_ciphertext
            from awface_tenant_liveness_credential
            where tenant_id = @tenant_id
            order by journey_type
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var credentials = new List<TenantCredential>();

        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(new TenantCredential(
                reader.GetGuid(0),
                Enum.Parse<JourneyType>(reader.GetString(1)),
                reader.GetString(2),
                _protector.Unprotect(reader.GetString(3))
            ));
        }

        return credentials;
    }

    private Tenant ReadTenant(NpgsqlDataReader reader, IReadOnlyList<TenantCredential> credentials)
    {
        return new Tenant(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<TenantStatus>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetBoolean(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : _protector.Unprotect(reader.GetString(15)),
            credentials,
            reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetFieldValue<DateTimeOffset>(17)
        );
    }

    private JourneySession ReadJourney(NpgsqlDataReader reader, IReadOnlyList<TenantCredential> credentials)
    {
        var tenant = new Tenant(
            reader.GetGuid(10),
            reader.GetString(11),
            reader.GetString(12),
            Enum.Parse<TenantStatus>(reader.GetString(13)),
            reader.GetString(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetBoolean(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : _protector.Unprotect(reader.GetString(25)),
            credentials,
            reader.GetFieldValue<DateTimeOffset>(26),
            reader.GetFieldValue<DateTimeOffset>(27)
        );

        return new JourneySession(
            reader.GetGuid(0),
            tenant,
            Enum.Parse<JourneyType>(reader.GetString(1)),
            Enum.Parse<LivenessEngine>(reader.GetString(28)),
            new JourneySubject(
                _protector.Unprotect(reader.GetString(2)),
                _protector.Unprotect(reader.GetString(3)),
                DateOnly.Parse(_protector.Unprotect(reader.GetString(4))),
                reader.GetString(5)
            ),
            Enum.Parse<JourneyStatus>(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            null,
            null,
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9)
        );
    }

    private static void AddJsonParameter(NpgsqlCommand command, string name, JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property))
        {
            command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, property.GetRawText());
            return;
        }

        command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, "{}");
    }

    private static DeviceLocation? ParseDeviceLocation(string? deviceLocationJson)
    {
        if (string.IsNullOrWhiteSpace(deviceLocationJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(deviceLocationJson);
            var root = document.RootElement;
            if (!TryGetDouble(root, "latitude", out var latitude) || !TryGetDouble(root, "longitude", out var longitude))
            {
                return null;
            }

            TryGetDouble(root, "accuracy", out var accuracy);
            DateTimeOffset? capturedAt = null;
            if (root.TryGetProperty("capturedAt", out var capturedAtElement)
                && capturedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(capturedAtElement.GetString(), out var parsedCapturedAt))
            {
                capturedAt = parsedCapturedAt;
            }

            return new DeviceLocation(latitude, longitude, accuracy, capturedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetDouble(JsonElement root, string propertyName, out double value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private sealed record DeviceLocation(double Latitude, double Longitude, double Accuracy, DateTimeOffset? CapturedAt);

    private static string NormalizeTheme(string? value)
    {
        var theme = value?.Trim().ToUpperInvariant();
        return theme is "DARK" ? "DARK" : "LIGHT";
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        var color = value?.Trim();
        return string.IsNullOrWhiteSpace(color) ? fallback : color;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private object ProtectOptional(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null ? DBNull.Value : _protector.Protect(normalized);
    }

    private const string JourneySelectSql = """
        select j.id, j.journey_type::text, j.cpf_ciphertext, j.full_name_ciphertext, j.birth_date_ciphertext,
               j.external_client_id, j.status::text, j.appkey, j.created_at, j.updated_at,
               t.id, t.name, t.integration_token, t.status::text, t.terms_url, t.privacy_url, t.logo_base64,
               t.theme, t.primary_color, t.secondary_color, t.callback_url, t.secure_callback_token,
               t.callback_oauth_enabled, t.callback_oauth_token_url, t.callback_oauth_client_id, t.callback_oauth_client_secret_ciphertext,
               t.created_at, t.updated_at, j.liveness_engine
        from awface_liveness_journey j
        join awface_tenant t on t.id = j.tenant_id
        """;
}
