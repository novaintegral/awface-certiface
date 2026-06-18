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
                add column if not exists secondary_color text not null default '#315f88';

            alter table awface_liveness_journey
                add column if not exists appkey_created_at timestamptz,
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

            create unique index if not exists ux_awface_liveness_journey_launch_token_hash
                on awface_liveness_journey (launch_token_hash)
                where launch_token_hash is not null;

            create index if not exists ix_awface_liveness_journey_launch_expires_at
                on awface_liveness_journey (launch_expires_at)
                where launch_token_hash is not null;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Schema AWFace verificado para jornada autônoma.");
    }
}
