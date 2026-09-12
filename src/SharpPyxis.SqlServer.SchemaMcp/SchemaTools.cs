using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Read-only tools with fixed queries, bound to one database at startup. No tool accepts free SQL.
/// Object names are schema-qualified: the database is the scope, and the rights granted to the
/// login are what narrows it.
/// </summary>
internal sealed class SchemaTools(SchemaSettings settings)
{
    // The object types the server exposes, as sys.objects spells them.
    private const string ExposedTypes = "'U', 'V', 'P', 'FN', 'IF', 'TF', 'SO'";

    // Shared by every query that counts, spreads or lists, so the three always see the same rows.
    private const string ObjectFilter = """
          and (@schema is null or s.name = @schema)
          and (@namePattern is null or o.name like @namePattern escape '\')
          and (@modifiedSince is null or o.modify_date >= @modifiedSince)
          and (@objectType is null
               or (@objectType = 'table'     and o.type = 'U')
               or (@objectType = 'view'      and o.type = 'V')
               or (@objectType = 'procedure' and o.type = 'P')
               or (@objectType = 'function'  and o.type in ('FN', 'IF', 'TF'))
               or (@objectType = 'sequence'  and o.type = 'SO'))
        """;

    // How many rows of a spread are worth reading: beyond that, the tail is counted in one line.
    private const int SpreadRows = 10;

    // Above this share, filtering on the leading schema still leaves most of the database, so the
    // spread by schema has told the caller nothing usable and the name prefixes are tried instead.
    private const double DominantShare = 0.60;

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

    /// <summary>Lists the objects of the database, filtered on schema, name, type or last change.</summary>
    [McpServerTool(Name = "list_objects", ReadOnly = true, Idempotent = true)]
    [Description("Lists the tables, views, procedures, functions and sequences of the database, with their "
               + "schema and last modification date. A large database holds tens of thousands of objects, so "
               + "filter whenever the request names a schema, a type or part of a name. When nothing in the "
               + "request tells you what to filter on, ask the user rather than guess. An unfiltered call that "
               + "matches too many objects returns how they are spread rather than the rows, which is what to "
               + "read to choose a filter.")]
    public async Task<string> ListObjects(
        [Description("Only objects of this schema. Optional, exact match.")]
        string? schema = null,
        [Description("Only objects whose name matches. Optional, substring by default.")]
        string? name = null,
        [Description("How to match the name: 'contains' (default) or 'equals'. Both follow the collation of "
                   + "the database, which is usually case-insensitive: 'equals' is not a strict comparison.")]
        string nameMatch = "contains",
        [Description("Only objects of this kind: 'table', 'view', 'procedure', 'function' or 'sequence'. Optional.")]
        string? objectType = null,
        [Description("Only objects modified since this date (ISO 8601). Optional.")]
        DateTime? modifiedSince = null,
        [Description("Take at most this many rows, and return them even when many more match. Use it when you "
                   + "deliberately want a sample or the last N changes.")]
        int? limit = null,
        [Description("Sort by modification date, most recent first, instead of by name. Use it for 'the last N changes'.")]
        bool mostRecentFirst = false,
        [Description("Return only how many objects match, and how they are spread, without any row.")]
        bool countOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var namePattern = BuildNamePattern(name, nameMatch);
        var spread = await ReadSpreadAsync(connection, schema, namePattern, objectType, modifiedSince, cancellationToken);

        if (spread.Total == 0)
            return "No object matches.";

        // A caller who passed a limit asked for that many rows and gets them; one who passed none
        // asked for everything, and everything does not fit.
        if (countOnly || (limit is null && spread.Total > settings.MaxResults))
            return await DescribeSpreadAsync(connection, spread, schema, namePattern, objectType, modifiedSince, cancellationToken);

        return await ReadRowsAsync(
            connection, Math.Min(limit ?? settings.MaxResults, settings.MaxResults),
            schema, namePattern, objectType, modifiedSince, mostRecentFirst, spread.Total, cancellationToken);
    }

    private async Task<string> ReadRowsAsync(
        SqlConnection connection, int limit, string? schema, string? namePattern, string? objectType,
        DateTime? modifiedSince, bool mostRecentFirst, int total, CancellationToken cancellationToken)
    {
        // The NULL first sort key leaves the order to schema and name unless mostRecentFirst is set.
        var query = $"""
            select top (@limit) s.name, o.name, o.type_desc, o.modify_date
            from sys.objects as o
            join sys.schemas as s on s.schema_id = o.schema_id
            where o.type in ({ExposedTypes})
            {ObjectFilter}
            order by case when @mostRecentFirst = 1 then o.modify_date end desc,
                     s.name,
                     o.name;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@mostRecentFirst", SqlDbType.Bit).Value = mostRecentFirst;
        AddFilterParameters(command, schema, namePattern, objectType, modifiedSince);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var inventory = new StringBuilder();
        var returned = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            inventory.Append(CultureInfo.InvariantCulture,
                $"{reader.GetString(2)}\t{reader.GetString(0)}.{reader.GetString(1)}\t{reader.GetDateTime(3):yyyy-MM-dd HH:mm:ss}");
            inventory.AppendLine();
            returned++;
        }

        return returned < total
            ? $"{inventory}({returned} of {total} matching objects.)"
            : inventory.ToString();
    }

    // One pass gives the total and both spreads: a database holds far fewer (schema, type) pairs
    // than objects.
    private static async Task<Spread> ReadSpreadAsync(
        SqlConnection connection, string? schema, string? namePattern, string? objectType,
        DateTime? modifiedSince, CancellationToken cancellationToken)
    {
        var query = $"""
            select s.name, o.type_desc, count(*)
            from sys.objects as o
            join sys.schemas as s on s.schema_id = o.schema_id
            where o.type in ({ExposedTypes})
            {ObjectFilter}
            group by s.name, o.type_desc;
            """;

        await using var command = new SqlCommand(query, connection);
        AddFilterParameters(command, schema, namePattern, objectType, modifiedSince);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var bySchema = new Dictionary<string, int>(StringComparer.Ordinal);
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            var (schemaName, typeName, count) = (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));

            bySchema[schemaName] = bySchema.GetValueOrDefault(schemaName) + count;
            byType[typeName] = byType.GetValueOrDefault(typeName) + count;
            total += count;
        }

        return new Spread(total, bySchema, byType);
    }

    private async Task<string> DescribeSpreadAsync(
        SqlConnection connection, Spread spread, string? schema, string? namePattern, string? objectType,
        DateTime? modifiedSince, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine($"{spread.Total} objects match, more than the {settings.MaxResults} this server returns at once.");
        report.AppendLine();
        AppendBreakdown(report, "By schema", spread.BySchema, spread.Total);
        AppendBreakdown(report, "By type", spread.ByType, spread.Total);

        // When one schema holds nearly everything, filtering on it changes little: the names are
        // the only remaining axis.
        var leading = spread.BySchema.OrderByDescending(entry => entry.Value).First();
        if (spread.BySchema.Count == 1 || (double)leading.Value / spread.Total > DominantShare)
        {
            var prefixes = await ReadPrefixesAsync(connection, schema, namePattern, objectType, modifiedSince, cancellationToken);
            if (prefixes.Count > 0)
                AppendBreakdown(report, "By name prefix (observed in the names, not a structure of the database)",
                    prefixes, spread.Total);
            else
                report.AppendLine("Names carry no usable prefix: narrow with part of a name.");
        }

        report.Append("Narrow with schema, name or objectType, or pass limit to take the first rows anyway.");
        return report.ToString();
    }

    private static void AppendBreakdown(StringBuilder report, string caption, Dictionary<string, int> counts, int total)
    {
        var ordered = counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        report.AppendLine(ordered.Count > SpreadRows ? $"{caption} (top {SpreadRows} of {ordered.Count}):" : $"{caption}:");

        foreach (var entry in ordered.Take(SpreadRows))
            report.AppendLine($"{entry.Key}\t{entry.Value}\t{FormatShare(entry.Value, total)}");

        if (ordered.Count > SpreadRows)
        {
            var tail = ordered.Skip(SpreadRows).Sum(entry => entry.Value);
            report.AppendLine($"and {ordered.Count - SpreadRows} more\t{tail}\t{FormatShare(tail, total)}");
        }

        report.AppendLine();
    }

    // Invariant on purpose: a percent sign preceded by a narrow no-break space, which a French
    // machine produces, travels badly through the tools that read this output.
    private static string FormatShare(int count, int total) =>
        ((double)count / total).ToString("P1", CultureInfo.InvariantCulture);

    // Groups on what precedes the first underscore, a convention most long-lived databases carry.
    private static async Task<Dictionary<string, int>> ReadPrefixesAsync(
        SqlConnection connection, string? schema, string? namePattern, string? objectType,
        DateTime? modifiedSince, CancellationToken cancellationToken)
    {
        var query = $"""
            select left(o.name, charindex('_', o.name) - 1), count(*)
            from sys.objects as o
            join sys.schemas as s on s.schema_id = o.schema_id
            where o.type in ({ExposedTypes})
              and charindex('_', o.name) > 1
            {ObjectFilter}
            group by left(o.name, charindex('_', o.name) - 1);
            """;

        await using var command = new SqlCommand(query, connection);
        AddFilterParameters(command, schema, namePattern, objectType, modifiedSince);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var prefixes = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
            prefixes[reader.GetString(0)] = reader.GetInt32(1);

        return prefixes;
    }

    private static void AddFilterParameters(
        SqlCommand command, string? schema, string? namePattern, string? objectType, DateTime? modifiedSince)
    {
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = (object?)schema ?? DBNull.Value;
        command.Parameters.Add("@namePattern", SqlDbType.NVarChar, 300).Value = (object?)namePattern ?? DBNull.Value;
        command.Parameters.Add("@objectType", SqlDbType.NVarChar, 20).Value = (object?)objectType?.ToLowerInvariant() ?? DBNull.Value;
        command.Parameters.Add("@modifiedSince", SqlDbType.DateTime2).Value = (object?)modifiedSince ?? DBNull.Value;
    }

    /// <summary>Scripts one object with SMO, the engine SSMS uses.</summary>
    [McpServerTool(Name = "script_object", ReadOnly = true, Idempotent = true)]
    [Description("Returns the complete CREATE script of one object, as SSMS generates it: tables with "
               + "constraints, indexes and triggers; views, procedures and functions with their original text.")]
    public string ScriptObject(
        [Description("Object name, schema-qualified ('sales.orders'). An unqualified name is accepted "
                   + "when exactly one schema holds it.")]
        string objectName)
    {
        var (schema, name) = SplitObjectName(objectName);

        var resolved = ResolveSchema(schema, name);
        if (resolved.Length == 0)
            return $"Object {objectName} not found.";
        if (resolved.Length > 1)
            return $"Several schemas hold an object named {name}. Qualify it: {string.Join(", ", resolved.Select(s => $"{s}.{name}"))}.";

        using var sqlConnection = new SqlConnection(settings.ConnectionString);
        var server = new Server(new ServerConnection(sqlConnection));
        try
        {
            var database = server.Databases[settings.DatabaseName]
                ?? throw new InvalidOperationException($"Database {settings.DatabaseName} is not accessible.");

            var scriptable = FindScriptable(database, name, resolved[0]);
            if (scriptable is null)
                return $"Object {resolved[0]}.{name} cannot be scripted.";

            var statements = scriptable.Script(CreateScriptingOptions());
            return string.Join($"{Environment.NewLine}GO{Environment.NewLine}", statements.Cast<string>())
                 + $"{Environment.NewLine}GO{Environment.NewLine}";
        }
        finally
        {
            server.ConnectionContext.Disconnect();
        }
    }

    // Splits on the first dot only: a schema name cannot contain one unless it is quoted, which the
    // brackets are stripped for.
    private static (string? Schema, string Name) SplitObjectName(string objectName)
    {
        var trimmed = objectName.Trim();
        var separator = trimmed.IndexOf('.');

        return separator < 0
            ? (null, Unquote(trimmed))
            : (Unquote(trimmed[..separator]), Unquote(trimmed[(separator + 1)..]));
    }

    private static string Unquote(string identifier)
    {
        var trimmed = identifier.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
    }

    // Returns the schemas holding an object of that name: none, one, or several to disambiguate.
    private string[] ResolveSchema(string? schema, string name)
    {
        const string query = $"""
            select s.name
            from sys.objects as o
            join sys.schemas as s on s.schema_id = o.schema_id
            where o.type in ({ExposedTypes})
              and o.name = @name
              and (@schema is null or s.name = @schema)
            order by s.name;
            """;

        using var connection = new SqlConnection(settings.ConnectionString);
        connection.Open();

        using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = (object?)schema ?? DBNull.Value;

        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read())
            schemas.Add(reader.GetString(0));

        return [.. schemas];
    }

    // Escapes what LIKE would otherwise read as a pattern, so a name holding _ or % still matches.
    private static string? BuildNamePattern(string? name, string nameMatch)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var escaped = name.Replace(@"\", @"\\")
                          .Replace("[", @"\[")
                          .Replace("%", @"\%")
                          .Replace("_", @"\_");

        return string.Equals(nameMatch, "equals", StringComparison.OrdinalIgnoreCase)
            ? escaped
            : $"%{escaped}%";
    }

    // SMO collections only load what the login can see.
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

    private sealed record Spread(int Total, Dictionary<string, int> BySchema, Dictionary<string, int> ByType);
}
