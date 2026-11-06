using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

/// <summary>
/// The demo database, created on LocalDB once for the whole run from the script the repository
/// ships, and dropped afterwards. The tests read what a user would read, from the same objects.
/// </summary>
public sealed partial class DemoDatabase : IDisposable
{
    private const string Server = @"(localdb)\MSSQLLocalDB";
    private const string Name = "SchemaMcpTests";

    // Never read: the connection below is declared the way CONNECTION_STRING declares one, which
    // takes precedence over the file.
    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.SchemaMcp.Tests", "connections.dat");

    public DemoDatabase()
    {
        // A previous run stopped halfway leaves the database behind: start from nothing either way.
        ExecuteOnMaster($"""
            if db_id(N'{Name}') is not null
            begin
                alter database [{Name}] set single_user with rollback immediate;
                drop database [{Name}];
            end;
            create database [{Name}];
            """);

        using var connection = new SqlConnection(Entry.BuildConnectionString());
        connection.Open();

        RunScript(connection, "Create-DemoDatabase.sql");

        // The CLR objects need their assembly. TRUSTWORTHY lets it in under clr strict security without
        // changing anything on the instance, and stays on this database, dropped after the run. CLR need
        // not be enabled: the objects are created and read, never run.
        ExecuteOnMaster($"alter database [{Name}] set trustworthy on;");
        using (var command = new SqlCommand("create assembly clr_sample from @bits with permission_set = safe;", connection))
        {
            command.Parameters.Add("@bits", SqlDbType.VarBinary, -1).Value =
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SharpPyxis.SqlServer.SchemaMcp.ClrSample.dll"));
            command.ExecuteNonQuery();
        }

        RunScript(connection, "Create-ClrObjects.sql");
    }

    private static void RunScript(SqlConnection connection, string fileName)
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));

        foreach (var batch in BatchSeparator().Split(script).Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            using var command = new SqlCommand(batch, connection);
            command.ExecuteNonQuery();
        }
    }

    internal ConnectionEntry Entry { get; } = new()
    {
        Id = 1,
        Server = Server,
        Database = Name,
        Label = "demo database",
        TrustServerCertificate = true,
    };

    // No conventions file unless a test gives one: the path below never exists.
    internal SchemaTools CreateTools(
        int maxResults = SchemaSettings.DefaultMaxResults, string? conventionsPath = null, ConnectionEntry? entry = null) =>
        new(new SchemaSettings(maxResults, UnusedConfigPath, entry ?? Entry, conventionsPath ?? MissingConventionsPath),
            new ConnectionStore(UnusedConfigPath));

    private static readonly string MissingConventionsPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.SchemaMcp.Tests", "no-conventions.md");

    public void Dispose()
    {
        // Pooled connections would keep the database in use.
        SqlConnection.ClearAllPools();
        ExecuteOnMaster($"alter database [{Name}] set single_user with rollback immediate; drop database [{Name}];");
    }

    private void ExecuteOnMaster(string sql)
    {
        using var connection = new SqlConnection(Entry.BuildConnectionString("master"));
        connection.Open();

        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    // GO is not T-SQL: SSMS and sqlcmd split on it, and so does this.
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();
}

/// <summary>Shares one demo database between every test class.</summary>
[CollectionDefinition(Name)]
public sealed class DemoDatabaseCollection : ICollectionFixture<DemoDatabase>
{
    public const string Name = "demo database";
}

internal static class ToolOutput
{
    // The first line names the target and the header names the columns; the rows of a listing are the
    // other lines holding a tab.
    public static string[] Rows(string result) =>
        [.. result.Split('\n').Skip(1).Where(line => line.Contains('\t') && !line.StartsWith("type\t"))];
}
