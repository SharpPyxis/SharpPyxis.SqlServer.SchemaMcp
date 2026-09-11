using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Server configuration, supplied by the MCP client through environment variables.
/// The schema is fixed here and never passed as a tool parameter, so the model cannot change it.
/// </summary>
internal sealed record SchemaSettings(string ConnectionString, string DatabaseName, string SchemaName)
{
    /// <summary>
    /// Reads <c>CONNECTION_STRING</c> and <c>DDL_SCHEMA</c>. The database is taken from the
    /// connection string, which must therefore name one.
    /// </summary>
    public static SchemaSettings FromEnvironment()
    {
        var connectionString = ReadRequired("CONNECTION_STRING");
        var schemaName = ReadRequired("DDL_SCHEMA");
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("CONNECTION_STRING must specify Database (or Initial Catalog).");

        return new SchemaSettings(connectionString, databaseName, schemaName);
    }

    private static string ReadRequired(string variableName) =>
        Environment.GetEnvironmentVariable(variableName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {variableName} is missing.");
}
