using Npgsql;

namespace Relay.Server.Data;

public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct);
}

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

public static class SchemaBootstrapper
{
    public static async Task ApplyAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        var assembly = typeof(SchemaBootstrapper).Assembly;
        const string resourceName = "Relay.Server.Data.schema.sql";
        string sql;
        await using (var stream = assembly.GetManifestResourceStream(resourceName)!)
        using (var reader = new StreamReader(stream))
            sql = await reader.ReadToEndAsync(ct);

        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
