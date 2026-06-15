using AWFace.Api.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AWFace.Api.Data;

public sealed class AwfaceDb
{
    private readonly string _connectionString;
    private readonly string _schema;

    public AwfaceDb(IConfiguration configuration, IOptions<AwfaceOptions> options)
    {
        _connectionString = configuration.GetConnectionString("Awface")
            ?? throw new InvalidOperationException("ConnectionStrings:Awface não configurada.");
        _schema = options.Value.Schema;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(_schema))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"set search_path to {QuoteIdentifier(_schema)}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
