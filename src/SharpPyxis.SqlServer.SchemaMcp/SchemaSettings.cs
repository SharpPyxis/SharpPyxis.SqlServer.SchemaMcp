using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// What the server reads from its environment: how much it may return, where its connections are
/// stored, and the single connection an environment variable may declare instead of that file.
/// </summary>
internal sealed record SchemaSettings(int MaxResults, string ConfigPath, ConnectionEntry? EnvironmentConnection)
{
    /// <summary>How many rows a listing tool returns at most when nothing else caps it.</summary>
    public const int DefaultMaxResults = 200;

    /// <summary>
    /// Reads <c>DDL_MAX_RESULTS</c>, <c>DDL_CONFIG_PATH</c> and <c>CONNECTION_STRING</c>, all
    /// optional. <c>CONNECTION_STRING</c> declares one connection without the encrypted file, which
    /// is how the server runs where DPAPI does not.
    /// </summary>
    public static SchemaSettings FromEnvironment()
    {
        var configPath = Environment.GetEnvironmentVariable("DDL_CONFIG_PATH") is { Length: > 0 } path
            ? path
            : ConnectionStore.DefaultPath;

        return new SchemaSettings(ReadMaxResults(), configPath, ReadEnvironmentConnection());
    }

    // A cap the model could raise is no cap at all, so it lives in the environment like the
    // connections do. An unusable value stops the server at startup rather than at the first call.
    private static int ReadMaxResults()
    {
        if (Environment.GetEnvironmentVariable("DDL_MAX_RESULTS") is not { Length: > 0 } value)
            return DefaultMaxResults;

        if (!int.TryParse(value, out var maxResults) || maxResults <= 0)
            throw new InvalidOperationException($"DDL_MAX_RESULTS must be a positive integer, not '{value}'.");

        return maxResults;
    }

    private static ConnectionEntry? ReadEnvironmentConnection()
    {
        if (Environment.GetEnvironmentVariable("CONNECTION_STRING") is not { Length: > 0 } connectionString)
            return null;

        var builder = new SqlConnectionStringBuilder(connectionString);
        var label = Environment.GetEnvironmentVariable("DDL_DESCRIPTION") is { Length: > 0 } value ? value : null;

        return new ConnectionEntry
        {
            Id = 0,
            Server = builder.DataSource,
            Database = builder.InitialCatalog is { Length: > 0 } catalog ? catalog : null,
            Login = builder.IntegratedSecurity ? null : builder.UserID,
            Password = builder.IntegratedSecurity ? null : builder.Password,
            Label = label,
            TrustServerCertificate = builder.TrustServerCertificate,
        };
    }
}
