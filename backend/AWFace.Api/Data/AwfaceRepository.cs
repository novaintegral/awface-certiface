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
                   theme, primary_color, secondary_color, callback_url, secure_callback_token, created_at, updated_at
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
                    theme, primary_color, secondary_color, callback_url, secure_callback_token, created_at, updated_at
                )
                values (
                    @id, @name, @integration_token, cast(@status as tenant_status), @terms_url, @privacy_url, @logo_base64,
                    @theme, @primary_color, @secondary_color, @callback_url, @secure_callback_token, now(), now()
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
        command.CommandText = JourneySelectSql + " where j.appkey = @appkey";
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

    public async Task<JourneySession> CreateJourneyAsync(Tenant tenant, JourneyStartRequest request, string? userAgent, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        var journeyId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_journey (
                id, tenant_id, journey_type, cpf_hash, cpf_ciphertext, full_name_ciphertext,
                birth_date_ciphertext, external_client_id, status, user_agent, created_at, updated_at
            )
            values (
                @id, @tenant_id, cast(@journey_type as journey_type), @cpf_hash, @cpf_ciphertext, @full_name_ciphertext,
                @birth_date_ciphertext, @external_client_id, 'CREATED', @user_agent, now(), now()
            )
            """;
        command.Parameters.AddWithValue("id", journeyId);
        command.Parameters.AddWithValue("tenant_id", tenant.Id);
        command.Parameters.AddWithValue("journey_type", request.JourneyType.ToString());
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
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        var journeyId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into awface_liveness_journey (
                id, tenant_id, journey_type, cpf_hash, cpf_ciphertext, full_name_ciphertext,
                birth_date_ciphertext, external_client_id, status, user_agent,
                launch_token_hash, launch_expires_at, host_reference, host_metadata,
                created_at, updated_at
            )
            values (
                @id, @tenant_id, cast(@journey_type as journey_type), @cpf_hash, @cpf_ciphertext, @full_name_ciphertext,
                @birth_date_ciphertext, @external_client_id, 'CREATED', @user_agent,
                @launch_token_hash, @launch_expires_at, @host_reference, cast(@host_metadata as jsonb),
                now(), now()
            )
            """;
        command.Parameters.AddWithValue("id", journeyId);
        command.Parameters.AddWithValue("tenant_id", tenant.Id);
        command.Parameters.AddWithValue("journey_type", request.JourneyType.ToString());
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
                  and launch_consumed_at is null
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

    public async Task<JourneySession?> RegisterConsentAsync(Guid journeyId, ConsentDecision decision, string termsUrl, string privacyUrl, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var consentCommand = connection.CreateCommand())
        {
            consentCommand.Transaction = transaction;
            consentCommand.CommandText = """
                insert into awface_liveness_consent (journey_id, decision, decided_at, ip_address, user_agent, terms_url, privacy_url)
                values (@journey_id, cast(@decision as consent_decision), now(), cast(@ip_address as inet), @user_agent, @terms_url, @privacy_url)
                """;
            consentCommand.Parameters.AddWithValue("journey_id", journeyId);
            consentCommand.Parameters.AddWithValue("decision", decision.ToString());
            consentCommand.Parameters.AddWithValue("ip_address", (object?)ipAddress ?? DBNull.Value);
            consentCommand.Parameters.AddWithValue("user_agent", (object?)userAgent ?? DBNull.Value);
            consentCommand.Parameters.AddWithValue("terms_url", termsUrl);
            consentCommand.Parameters.AddWithValue("privacy_url", privacyUrl);
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update awface_liveness_journey
            set appkey = @appkey,
                status = 'APPKEY_CREATED',
                updated_at = now()
            where id = @journey_id
            """;
        command.Parameters.AddWithValue("journey_id", journeyId);
        command.Parameters.AddWithValue("appkey", appkey);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetJourneyByIdAsync(connection, journeyId, cancellationToken);
    }

    public async Task MarkJourneyCompletedAsync(Guid journeyId, JsonDocument result, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var resultCommand = connection.CreateCommand())
        {
            resultCommand.Transaction = transaction;
            resultCommand.CommandText = """
                insert into awface_liveness_result (
                    journey_id, provider_status, id_externo_cliente, certiface_payload,
                    facecaptcha_payload, photos_payload, raw_payload, created_at
                )
                values (
                    @journey_id, @provider_status, @id_externo_cliente, cast(@certiface_payload as jsonb),
                    cast(@facecaptcha_payload as jsonb), cast(@photos_payload as jsonb), cast(@raw_payload as jsonb), now()
                )
                """;
            var root = result.RootElement;
            resultCommand.Parameters.AddWithValue("journey_id", journeyId);
            resultCommand.Parameters.AddWithValue("provider_status", root.TryGetProperty("status", out var status) ? status.GetString() ?? "Completo" : "Completo");
            resultCommand.Parameters.AddWithValue("id_externo_cliente", root.TryGetProperty("idExternoCliente", out var externalId) ? externalId.GetString() ?? (object)DBNull.Value : DBNull.Value);
            AddJsonParameter(resultCommand, "certiface_payload", root, "certifaceID");
            AddJsonParameter(resultCommand, "facecaptcha_payload", root, "facecaptcha");
            AddJsonParameter(resultCommand, "photos_payload", root, "fotos");
            resultCommand.Parameters.AddWithValue("raw_payload", NpgsqlDbType.Jsonb, result.RootElement.GetRawText());
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
                   theme, primary_color, secondary_color, callback_url, secure_callback_token, created_at, updated_at
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
                   theme, primary_color, secondary_color, callback_url, secure_callback_token, created_at, updated_at
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
            credentials,
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetFieldValue<DateTimeOffset>(13)
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
            credentials,
            reader.GetFieldValue<DateTimeOffset>(22),
            reader.GetFieldValue<DateTimeOffset>(23)
        );

        return new JourneySession(
            reader.GetGuid(0),
            tenant,
            Enum.Parse<JourneyType>(reader.GetString(1)),
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

    private const string JourneySelectSql = """
        select j.id, j.journey_type::text, j.cpf_ciphertext, j.full_name_ciphertext, j.birth_date_ciphertext,
               j.external_client_id, j.status::text, j.appkey, j.created_at, j.updated_at,
               t.id, t.name, t.integration_token, t.status::text, t.terms_url, t.privacy_url, t.logo_base64,
               t.theme, t.primary_color, t.secondary_color, t.callback_url, t.secure_callback_token, t.created_at, t.updated_at
        from awface_liveness_journey j
        join awface_tenant t on t.id = j.tenant_id
        """;
}
