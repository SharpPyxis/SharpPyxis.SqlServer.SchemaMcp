using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Tells when a login can read the data of a database. A yes is certain; a no is never claimed, since
/// rights granted table by table escape any check made at the level of the database.
/// </summary>
internal static class DataAccess
{
    /// <summary>
    /// Returns why the login behind <paramref name="connectionString"/> can read the data, or null when
    /// nothing says it can — which is not a guarantee that it cannot.
    /// </summary>
    public static string? Check(string connectionString, bool databaseLevel)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return Check(connection, databaseLevel);
        }
        catch (SqlException)
        {
            // The tool that needs this connection will report why it cannot open. Silence claims nothing.
            return null;
        }
    }

    /// <summary>Same check, on a connection already open.</summary>
    public static string? Check(SqlConnection connection, bool databaseLevel)
    {
        try
        {
            // sysadmin reads everything. A SELECT held on the database — db_datareader, db_owner or a
            // grant — reads every table in it.
            using var command = new SqlCommand(
                "select is_srvrolemember('sysadmin'), has_perms_by_name(db_name(), 'DATABASE', 'SELECT');", connection);
            using var reader = command.ExecuteReader();
            reader.Read();

            if (!reader.IsDBNull(0) && reader.GetInt32(0) == 1)
                return "sysadmin";

            return databaseLevel && !reader.IsDBNull(1) && reader.GetInt32(1) == 1
                ? "SELECT on the database"
                : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>What the person choosing the login, and the model, are told.</summary>
    public static string Warning(string reason) =>
        $"This login can read the data of the database ({reason}): only the code of this server keeps an "
        + "agent to its structure. A login limited to VIEW DEFINITION makes that guarantee independent of the server.";
}
