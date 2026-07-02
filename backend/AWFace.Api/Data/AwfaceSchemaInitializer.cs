namespace AWFace.Api.Data;

public sealed class AwfaceSchemaInitializer
{
    private readonly AwfaceDb _db;
    private readonly ILogger<AwfaceSchemaInitializer> _logger;

    public AwfaceSchemaInitializer(AwfaceDb db, ILogger<AwfaceSchemaInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            alter table awface_tenant
                add column if not exists theme text not null default 'LIGHT',
                add column if not exists primary_color text not null default '#007060',
                add column if not exists secondary_color text not null default '#315f88',
                add column if not exists callback_oauth_enabled boolean not null default false,
                add column if not exists callback_oauth_token_url text,
                add column if not exists callback_oauth_client_id text,
                add column if not exists callback_oauth_client_secret_ciphertext text;

            alter table awface_liveness_journey
                add column if not exists appkey_created_at timestamptz,
                add column if not exists liveness_engine varchar(3) not null default 'V10',
                add column if not exists launch_token_hash text,
                add column if not exists launch_expires_at timestamptz,
                add column if not exists launch_consumed_at timestamptz,
                add column if not exists host_reference text,
                add column if not exists host_metadata jsonb;

            alter table awface_liveness_consent
                drop column if exists terms_url,
                drop column if exists privacy_url;

            do $$
            begin
                if exists (
                    select 1 from information_schema.columns
                    where table_schema = current_schema()
                      and table_name = 'awface_liveness_result'
                      and column_name = 'certiface_payload'
                ) and not exists (
                    select 1 from information_schema.columns
                    where table_schema = current_schema()
                      and table_name = 'awface_liveness_result'
                      and column_name = 'bureau_payload'
                ) then
                    alter table awface_liveness_result rename column certiface_payload to bureau_payload;
                end if;

                if exists (
                    select 1 from information_schema.columns
                    where table_schema = current_schema()
                      and table_name = 'awface_liveness_result'
                      and column_name = 'facecaptcha_payload'
                ) and not exists (
                    select 1 from information_schema.columns
                    where table_schema = current_schema()
                      and table_name = 'awface_liveness_result'
                      and column_name = 'liveness_payload'
                ) then
                    alter table awface_liveness_result rename column facecaptcha_payload to liveness_payload;
                end if;
            end $$;

            alter table awface_liveness_result
                add column if not exists bureau_payload jsonb,
                add column if not exists liveness_payload jsonb,
                add column if not exists location_latitude double precision,
                add column if not exists location_longitude double precision,
                add column if not exists location_accuracy double precision,
                add column if not exists location_captured_at timestamptz,
                drop column if exists face_image_base64,
                drop column if exists raw_payload,
                drop column if exists photos_payload;

            create table if not exists awface_liveness_face_asset (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                tenant_id uuid not null references awface_tenant(id),
                asset_type text not null,
                storage_key text not null,
                content_type text not null,
                sha256 text not null,
                size_bytes bigint not null,
                encryption_algorithm text not null default 'AES-256-GCM',
                created_at timestamptz not null default now(),
                unique (journey_id, asset_type)
            );

            alter table awface_liveness_face_asset
                add column if not exists encryption_algorithm text not null default 'AES-256-GCM';

            create table if not exists awface_liveness_appkey_history (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null unique,
                liveness_engine varchar(3) not null,
                attempt integer not null,
                issued_at timestamptz not null default now(),
                superseded_at timestamptz,
                unique (journey_id, attempt)
            );

            create index if not exists ix_awface_liveness_appkey_history_journey
                on awface_liveness_appkey_history (journey_id, issued_at);

            create table if not exists awface_liveness_submission (
                appkey text primary key,
                journey_id uuid not null references awface_liveness_journey(id),
                liveness_engine varchar(3) not null,
                provider_response jsonb not null,
                device_location jsonb,
                submitted_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_awface_liveness_submission_journey
                on awface_liveness_submission (journey_id, submitted_at);

            create table if not exists awface_liveness_attempt_history (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null,
                liveness_engine varchar(3) not null,
                event_type text not null,
                provider_response jsonb not null,
                device_location jsonb,
                created_at timestamptz not null default now()
            );

            create index if not exists ix_awface_liveness_attempt_history_journey
                on awface_liveness_attempt_history (journey_id, created_at);
            create table if not exists awface_provider_notification (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null,
                provider_status varchar(80) not null,
                attempts integer not null default 1,
                processing_started_at timestamptz,
                processed_at timestamptz,
                last_error text,
                received_at timestamptz not null default now(),
                unique (appkey)
            );

            create unique index if not exists ux_awface_liveness_journey_launch_token_hash
                on awface_liveness_journey (launch_token_hash)
                where launch_token_hash is not null;

            create index if not exists ix_awface_liveness_journey_launch_expires_at
                on awface_liveness_journey (launch_expires_at)
                where launch_token_hash is not null;

            create table if not exists awface_schema_migration (
                id text primary key,
                description text not null,
                applied_at timestamptz not null default now()
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ApplyMigrationAsync(
            connection,
            "202606180001_tenant_callback_oauth",
            "Adiciona autenticaÃ§Ã£o OAuth2 ao webhook do tenant.",
            """
            alter table awface_tenant
                add column if not exists callback_oauth_enabled boolean not null default false,
                add column if not exists callback_oauth_token_url text,
                add column if not exists callback_oauth_client_id text,
                add column if not exists callback_oauth_client_secret_ciphertext text;
            """,
            cancellationToken
        );
        await ApplyMigrationAsync(
            connection,
            "202606240001_liveness_engine",
            "Registra o engine FaceTec utilizado por cada jornada.",
            """
            alter table awface_liveness_journey
                add column if not exists liveness_engine varchar(3) not null default 'V10';

            update awface_liveness_journey
            set liveness_engine = 'V10'
            where liveness_engine is null or liveness_engine not in ('V9', 'V10');
            """,
            cancellationToken
        );
        await ApplyMigrationAsync(
            connection,
            "202606240002_liveness_appkey_history",
            "Preserva todas as appkeys emitidas para diagnÃ³stico das jornadas.",
            """
            create table if not exists awface_liveness_appkey_history (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null unique,
                liveness_engine varchar(3) not null,
                attempt integer not null,
                issued_at timestamptz not null default now(),
                superseded_at timestamptz,
                unique (journey_id, attempt)
            );

            create index if not exists ix_awface_liveness_appkey_history_journey
                on awface_liveness_appkey_history (journey_id, issued_at);

            insert into awface_liveness_appkey_history (
                journey_id, appkey, liveness_engine, attempt, issued_at
            )
            select
                id,
                appkey,
                liveness_engine,
                1,
                coalesce(appkey_created_at, updated_at, created_at)
            from awface_liveness_journey
            where appkey is not null
            on conflict (appkey) do nothing;
            """,
            cancellationToken
        );
        await ApplyMigrationAsync(
            connection,
            "202606240003_provider_completion_webhook",
            "Armazena submissÃµes de liveness e controla notificaÃ§Ãµes terminais da Certiface.",
            """
            create table if not exists awface_liveness_submission (
                appkey text primary key,
                journey_id uuid not null references awface_liveness_journey(id),
                liveness_engine varchar(3) not null,
                provider_response jsonb not null,
                device_location jsonb,
                submitted_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create index if not exists ix_awface_liveness_submission_journey
                on awface_liveness_submission (journey_id, submitted_at);

            create table if not exists awface_liveness_attempt_history (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null,
                liveness_engine varchar(3) not null,
                event_type text not null,
                provider_response jsonb not null,
                device_location jsonb,
                created_at timestamptz not null default now()
            );

            create index if not exists ix_awface_liveness_attempt_history_journey
                on awface_liveness_attempt_history (journey_id, created_at);
            create table if not exists awface_provider_notification (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null,
                provider_status varchar(80) not null,
                attempts integer not null default 1,
                processing_started_at timestamptz,
                processed_at timestamptz,
                last_error text,
                received_at timestamptz not null default now(),
                unique (appkey)
            );

            create unique index if not exists ux_awface_provider_notification_appkey
                on awface_provider_notification (appkey);
            """,
            cancellationToken
        );

        await ApplyMigrationAsync(
            connection,
            "202607020001_liveness_attempt_history",
            "Preserva historico append-only dos retornos do SDK liveness por jornada.",
            """
            create table if not exists awface_liveness_attempt_history (
                id uuid primary key default gen_random_uuid(),
                journey_id uuid not null references awface_liveness_journey(id),
                appkey text not null,
                liveness_engine varchar(3) not null,
                event_type text not null,
                provider_response jsonb not null,
                device_location jsonb,
                created_at timestamptz not null default now()
            );

            create index if not exists ix_awface_liveness_attempt_history_journey
                on awface_liveness_attempt_history (journey_id, created_at);
            """,
            cancellationToken
        );
        _logger.LogInformation("Schema AWFace verificado e migrations aplicadas.");
    }

    private async Task ApplyMigrationAsync(
        Npgsql.NpgsqlConnection connection,
        string id,
        string description,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "select exists(select 1 from awface_schema_migration where id = @id)";
        existsCommand.Parameters.AddWithValue("id", id);
        var alreadyApplied = (bool)(await existsCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        if (alreadyApplied)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationCommand = connection.CreateCommand())
        {
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = sql;
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var registerCommand = connection.CreateCommand())
        {
            registerCommand.Transaction = transaction;
            registerCommand.CommandText = """
                insert into awface_schema_migration (id, description)
                values (@id, @description)
                """;
            registerCommand.Parameters.AddWithValue("id", id);
            registerCommand.Parameters.AddWithValue("description", description);
            await registerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogInformation("Migration AWFace aplicada: {MigrationId} - {Description}", id, description);
    }
}
