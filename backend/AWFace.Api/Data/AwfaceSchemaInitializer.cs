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
