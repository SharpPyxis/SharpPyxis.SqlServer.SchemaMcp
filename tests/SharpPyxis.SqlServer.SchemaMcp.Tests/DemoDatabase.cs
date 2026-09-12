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

        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Create-DemoDatabase.sql"));

        using var connection = new SqlConnection(Entry.BuildConnectionString());
        connection.Open();

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

    internal SchemaTools CreateTools(int maxResults = SchemaSettings.DefaultMaxResults) =>
        new(new SchemaSettings(maxResults, UnusedConfigPath, Entry), new ConnectionStore(UnusedConfigPath));

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
    // The first line names the target; the rows of a listing are the lines holding a tab.
    public static string[] Rows(string result) =>
        [.. result.Split('\n').Skip(1).Where(line => line.Contains('\t'))];
}
