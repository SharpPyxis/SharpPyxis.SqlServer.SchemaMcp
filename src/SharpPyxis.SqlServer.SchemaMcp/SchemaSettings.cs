using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Server configuration, supplied by the MCP client through environment variables.
/// The connection is fixed here and never passed as a tool parameter, so the model cannot change it.
/// </summary>
internal sealed record SchemaSettings(
    string ConnectionString,
    string DatabaseName,
    string? Purpose,
    int MaxResults)
{
    /// <summary>How many rows a listing tool returns at most when nothing else caps it.</summary>
    public const int DefaultMaxResults = 200;

    /// <summary>
    /// Reads <c>CONNECTION_STRING</c>, required, plus <c>DDL_DESCRIPTION</c> and
    /// <c>DDL_MAX_RESULTS</c>, both optional. The database is taken from the connection string,
    /// which must therefore name one.
    /// </summary>
    public static SchemaSettings FromEnvironment()
    {
        var connectionString = ReadRequired("CONNECTION_STRING");
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("CONNECTION_STRING must specify Database (or Initial Catalog).");

        var purpose = Environment.GetEnvironmentVariable("DDL_DESCRIPTION") is { Length: > 0 } value ? value : null;

        return new SchemaSettings(connectionString, databaseName, purpose, ReadMaxResults());
    }

    // A cap the model could raise is no cap at all, so it lives in the environment like the
    // connection does. An unusable value stops the server at startup rather than at the first call.
    private static int ReadMaxResults()
    {
        if (Environment.GetEnvironmentVariable("DDL_MAX_RESULTS") is not { Length: > 0 } value)
            return DefaultMaxResults;

        if (!int.TryParse(value, out var maxResults) || maxResults <= 0)
            throw new InvalidOperationException($"DDL_MAX_RESULTS must be a positive integer, not '{value}'.");

        return maxResults;
    }

    /// <summary>
    /// What the model reads to tell this target from the others: the database, preceded by
    /// <c>DDL_DESCRIPTION</c> when one is set.
    /// </summary>
    public string Target =>
        Purpose is null
            ? $"database {DatabaseName}"
            : $"{Purpose} (database {DatabaseName})";

    private static string ReadRequired(string variableName) =>
        Environment.GetEnvironmentVariable(variableName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {variableName} is missing.");
}
