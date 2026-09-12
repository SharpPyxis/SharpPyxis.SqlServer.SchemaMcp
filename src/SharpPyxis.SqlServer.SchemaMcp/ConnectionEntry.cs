using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// One stored connection. The key is server, database and login: the login belongs to it so that two
/// accesses to the same database can coexist, which is what comparing a restricted login with a
/// wider one requires.
/// </summary>
internal sealed record ConnectionEntry
{
    /// <summary>Short identifier, assigned on creation and never reassigned.</summary>
    public required int Id { get; init; }

    public required string Server { get; init; }

    /// <summary>Null when the connection names a server only, the database being chosen later.</summary>
    public string? Database { get; init; }

    /// <summary>Null for Windows authentication.</summary>
    public string? Login { get; init; }

    public string? Password { get; init; }

    /// <summary>Free text naming the target in plain words, in place of the server and database.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Accepts a server certificate the machine does not trust. Common on an internal instance with a
    /// self-signed certificate, and refused by default since the client encrypts by default.
    /// </summary>
    public bool TrustServerCertificate { get; init; }

    /// <summary>What the model and the user read to tell one connection from another.</summary>
    public string Describe()
    {
        var target = Database is null ? Server : $"{Server} / {Database}";
        var identity = Login ?? "Windows authentication";

        return Label is null ? $"{target} as {identity}" : $"{Label} ({target} as {identity})";
    }

    /// <summary>
    /// Builds the connection string, <paramref name="database"/> overriding the stored one. A
    /// connection declared at server level needs that override before any tool can run.
    /// </summary>
    public string BuildConnectionString(string? database = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            ApplicationName = "SharpPyxis.SqlServer.SchemaMcp",
            TrustServerCertificate = TrustServerCertificate,
        };

        if ((database ?? Database) is { Length: > 0 } catalog)
            builder.InitialCatalog = catalog;

        if (Login is null)
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = Login;
            builder.Password = Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
