using AWFace.Api.Configuration;
using AWFace.Api.Security;
using AWFace.Api.Services;
using Microsoft.Extensions.Options;

namespace AWFace.Api.Data;

/// <summary>
/// Recriptografa com AES-256-GCM os dados gravados pelas versões 1.0.x (somente base64)
/// e recalcula o <c>cpf_hash</c> com HMAC. É idempotente e roda a cada inicialização.
/// </summary>
public sealed class SensitiveDataMigrator
{
    private const int BatchSize = 500;
    private const string LegacyFilter = "not like 'enc:v1:%'";

    private readonly AwfaceDb _db;
    private readonly SensitiveDataProtector _protector;
    private readonly ILogger<SensitiveDataMigrator> _logger;

    public SensitiveDataMigrator(AwfaceDb db, SensitiveDataProtector protector, ILogger<SensitiveDataMigrator> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var journeys = await MigrateJourneysAsync(cancellationToken);
        var credentials = await MigrateColumnAsync("awface_tenant_liveness_credential", "provider_pass_ciphertext", cancellationToken);
        var oauthSecrets = await MigrateColumnAsync("awface_tenant", "callback_oauth_client_secret_ciphertext", cancellationToken);

        if (journeys + credentials + oauthSecrets > 0)
        {
            _logger.LogWarning(
                "Dados sensíveis legados recriptografados com AES-256-GCM: jornadas={Journeys}, credenciais={Credentials}, segredosOAuth={OAuthSecrets}.",
                journeys,
                credentials,
                oauthSecrets
            );
        }
    }

    private async Task<int> MigrateJourneysAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);

        while (true)
        {
            var batch = new List<(Guid Id, string Cpf, string Name, string BirthDate)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    select id, cpf_ciphertext, full_name_ciphertext, birth_date_ciphertext
                    from awface_liveness_journey
                    where cpf_ciphertext {LegacyFilter}
                       or full_name_ciphertext {LegacyFilter}
                       or birth_date_ciphertext {LegacyFilter}
                    limit {BatchSize}
                    """;
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }
            }

            if (batch.Count == 0)
            {
                return total;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var row in batch)
            {
                var cpf = JourneyValidator.DigitsOnly(_protector.Unprotect(row.Cpf));
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    update awface_liveness_journey
                    set cpf_hash = @cpf_hash,
                        cpf_ciphertext = @cpf,
                        full_name_ciphertext = @name,
                        birth_date_ciphertext = @birth_date
                    where id = @id
                    """;
                update.Parameters.AddWithValue("id", row.Id);
                update.Parameters.AddWithValue("cpf_hash", _protector.Hash(cpf));
                update.Parameters.AddWithValue("cpf", _protector.Protect(cpf));
                update.Parameters.AddWithValue("name", _protector.Protect(_protector.Unprotect(row.Name)));
                update.Parameters.AddWithValue("birth_date", _protector.Protect(_protector.Unprotect(row.BirthDate)));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            total += batch.Count;
        }
    }

    private async Task<int> MigrateColumnAsync(string table, string column, CancellationToken cancellationToken)
    {
        var total = 0;
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);

        while (true)
        {
            var batch = new List<(Guid Id, string Value)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    select id, {column}
                    from {table}
                    where {column} is not null and {column} {LegacyFilter}
                    limit {BatchSize}
                    """;
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add((reader.GetGuid(0), reader.GetString(1)));
                }
            }

            if (batch.Count == 0)
            {
                return total;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var row in batch)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"update {table} set {column} = @value where id = @id";
                update.Parameters.AddWithValue("id", row.Id);
                update.Parameters.AddWithValue("value", _protector.Protect(_protector.Unprotect(row.Value)));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            total += batch.Count;
        }
    }
}

/// <summary>Cria o primeiro administrador a partir de Awface:Admin quando não há nenhum usuário ativo.</summary>
public sealed class AdminBootstrapper
{
    private readonly AwfaceRepository _repository;
    private readonly AwfaceOptions _options;
    private readonly ILogger<AdminBootstrapper> _logger;

    public AdminBootstrapper(AwfaceRepository repository, IOptions<AwfaceOptions> options, ILogger<AdminBootstrapper> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (await _repository.HasActiveAdminAsync(cancellationToken))
        {
            return;
        }

        var admin = _options.Admin;
        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password))
        {
            _logger.LogWarning(
                "Nenhum administrador cadastrado. Defina AWFACE_ADMIN_EMAIL e AWFACE_ADMIN_PASSWORD para criar o primeiro acesso."
            );
            return;
        }

        await _repository.CreateAdminAsync(admin.Email, PasswordHasher.Hash(admin.Password), cancellationToken);
        _logger.LogWarning(
            "Administrador inicial criado a partir da configuração. Remova AWFACE_ADMIN_PASSWORD do ambiente após o primeiro acesso."
        );
    }
}

/// <summary>Impede que a API suba fora de Development com chaves ausentes, fracas ou de exemplo.</summary>
public static class StartupSecurityValidator
{
    private static readonly string[] KnownDefaultAdminPasswords = ["Awface@123", "Intsis@1999", "admin", "123456"];

    public static void Validate(AwfaceOptions options, IHostEnvironment environment, ILogger logger)
    {
        var problems = new List<string>();

        if (KeyMaterial.IsWeak(options.Security.DataEncryptionKey))
        {
            problems.Add("Awface:Security:DataEncryptionKey (AWFACE_DATA_ENCRYPTION_KEY) ausente, fraca ou de exemplo.");
        }

        if (KeyMaterial.IsWeak(options.FaceStorage.EncryptionKey))
        {
            problems.Add("Awface:FaceStorage:EncryptionKey (AWFACE_FACE_STORAGE_ENCRYPTION_KEY) ausente, fraca ou de exemplo.");
        }

        if (!string.IsNullOrWhiteSpace(options.Security.TokenSigningKey) && KeyMaterial.IsWeak(options.Security.TokenSigningKey))
        {
            problems.Add("Awface:Security:TokenSigningKey (AWFACE_TOKEN_SIGNING_KEY) fraca ou de exemplo.");
        }

        if (!string.IsNullOrWhiteSpace(options.Admin.Password)
            && (options.Admin.Password.Length < 12
                || KnownDefaultAdminPasswords.Any(item => string.Equals(item, options.Admin.Password, StringComparison.Ordinal))))
        {
            problems.Add("Awface:Admin:Password (AWFACE_ADMIN_PASSWORD) é conhecida ou tem menos de 12 caracteres.");
        }

        if (problems.Count == 0)
        {
            return;
        }

        if (environment.IsDevelopment())
        {
            foreach (var problem in problems)
            {
                logger.LogWarning("Configuração insegura aceita apenas em Development: {Problem}", problem);
            }

            return;
        }

        throw new InvalidOperationException(
            "Configuração de segurança inválida para o ambiente " + environment.EnvironmentName + ":" + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Select(item => " - " + item))
        );
    }
}
