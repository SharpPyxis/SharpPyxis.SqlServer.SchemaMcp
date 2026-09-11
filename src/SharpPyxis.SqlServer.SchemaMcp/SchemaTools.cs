using System.ComponentModel;
using System.Data;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Two read-only tools with fixed queries, bound to one target at startup. No tool accepts free SQL.
/// </summary>
internal sealed class SchemaTools(SchemaSettings settings)
{
    /// <summary>
    /// Builds the tools with the target appended to each description. A client that runs several
    /// instances of this server shows the model one set of tools per target, and the description is
    /// what tells them apart.
    /// </summary>
    public IEnumerable<McpServerTool> CreateTools() =>
        [CreateTool(nameof(ListObjects)), CreateTool(nameof(ScriptObject))];

    private McpServerTool CreateTool(string methodName)
    {
        var method = typeof(SchemaTools).GetMethod(methodName)!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        return McpServerTool.Create(method, this, new McpServerToolCreateOptions
        {
            Description = $"{description} Target: {settings.Target}.",
        });
    }

    /// <summary>Lists the objects of the configured schema, optionally filtered on their last change.</summary>
    [McpServerTool(Name = "list_objects", ReadOnly = true, Idempotent = true)]
    [Description("Lists the tables, views, procedures, functions and sequences of the configured schema, "
               + "with their last modification date. Optional filter on that date.")]
    public async Task<string> ListObjects(
        [Description("Only return objects modified since this date (ISO 8601). Optional.")]
        DateTime? modifiedSince = null,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT o.type_desc, o.name, o.modify_date
            FROM sys.objects AS o
            WHERE o.schema_id = SCHEMA_ID(@schemaName)
              AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SO')
              AND (@modifiedSince IS NULL OR o.modify_date >= @modifiedSince)
            ORDER BY o.type_desc, o.name;
            """;

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@schemaName", SqlDbType.NVarChar, 128).Value = settings.SchemaName;
        command.Parameters.Add("@modifiedSince", SqlDbType.DateTime2).Value = (object?)modifiedSince ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var inventory = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
            inventory.AppendLine($"{reader.GetString(0)}\t{reader.GetString(1)}\t{reader.GetDateTime(2):yyyy-MM-dd HH:mm:ss}");

        return inventory.Length > 0 ? inventory.ToString() : "No object matches.";
    }

    /// <summary>Scripts one object of the configured schema with SMO, the engine SSMS uses.</summary>
    [McpServerTool(Name = "script_object", ReadOnly = true, Idempotent = true)]
    [Description("Returns the complete CREATE script of an object of the configured schema, as SSMS generates it: "
               + "tables with constraints, indexes and triggers; views, procedures and functions with their original text.")]
    public string ScriptObject(
        [Description("Object name, without the schema prefix.")] string objectName)
    {
        using var sqlConnection = new SqlConnection(settings.ConnectionString);
        var server = new Server(new ServerConnection(sqlConnection));
        try
        {
            var database = server.Databases[settings.DatabaseName]
                ?? throw new InvalidOperationException($"Database {settings.DatabaseName} is not accessible.");

            var scriptable = FindScriptable(database, objectName, settings.SchemaName);
            if (scriptable is null)
                return $"Object {settings.SchemaName}.{objectName} not found.";

            var statements = scriptable.Script(CreateScriptingOptions());
            return string.Join($"{Environment.NewLine}GO{Environment.NewLine}", statements.Cast<string>())
                 + $"{Environment.NewLine}GO{Environment.NewLine}";
        }
        finally
        {
            server.ConnectionContext.Disconnect();
        }
    }

    // SMO collections only load what the login can see: here, the one schema it is granted.
    private static IScriptable? FindScriptable(Database database, string name, string schema) =>
        (IScriptable?)database.Tables[name, schema]
        ?? (IScriptable?)database.Views[name, schema]
        ?? (IScriptable?)database.StoredProcedures[name, schema]
        ?? (IScriptable?)database.UserDefinedFunctions[name, schema]
        ?? database.Sequences[name, schema];

    // Close to the SSMS defaults of "Generate and Publish Scripts".
    private static ScriptingOptions CreateScriptingOptions() => new()
    {
        SchemaQualify = true,
        DriAll = true,
        Indexes = true,
        Triggers = true,
        ExtendedProperties = true,
        NoCollation = true,
        IncludeHeaders = false,
    };
}
