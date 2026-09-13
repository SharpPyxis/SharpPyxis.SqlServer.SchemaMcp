using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
internal sealed partial class SchemaTools(SchemaSettings settings, ConnectionStore store)
{
    // The connection every tool works against. Null until one is chosen, which is what makes the
    // model ask instead of picking. A reference assignment is atomic, which is all the concurrency
    // this needs.
    private ActiveConnection? _active = OpenByDefault(settings, store);

    // The object types the server exposes, as sys.objects spells them.
    private const string ExposedTypes = "'U', 'V', 'P', 'FN', 'IF', 'TF', 'SO'";

    // The word a type is written with in every result: the one objectType accepts, so what a result
    // shows can be passed back as a filter, and a few tokens where the catalog name costs several.
    // Functions keep their kind, which decides how SQL calls them. Any other type keeps its catalog
    // name, lower-cased.
    private static string TypeWord(string typeDesc) => typeDesc switch
    {
        "USER_TABLE" => "table",
        "VIEW" => "view",
        "SQL_STORED_PROCEDURE" => "procedure",
        "SQL_SCALAR_FUNCTION" => "scalar function",
        "SQL_INLINE_TABLE_VALUED_FUNCTION" => "inline function",
        "SQL_TABLE_VALUED_FUNCTION" => "table function",
        "SQL_TRIGGER" => "trigger",
        "SEQUENCE_OBJECT" => "sequence",
        _ => typeDesc.ToLowerInvariant().Replace('_', ' '),
    };

    // The number of rows of a table as the catalog keeps it, readable under VIEW DEFINITION: the rows
    // themselves are neither read nor counted. Null for anything but a table.
    private static string TableRows(string alias) =>
        $"case when {alias}.type = 'U' then (select sum(p.rows) from sys.partitions as p "
        + $"where p.object_id = {alias}.object_id and p.index_id in (0, 1)) end";

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
    public IEnumerable<McpServerTool> CreateTools()
    {
        List<McpServerTool> tools =
        [
            CreateTool(nameof(UseConnection)), CreateTool(nameof(ServerInfo)),
            CreateTool(nameof(ListObjects)), CreateTool(nameof(DescribeObject)), CreateTool(nameof(ScriptObject)),
            CreateTool(nameof(FindReferences)),
            CreateTool(nameof(SearchModules)), CreateTool(nameof(FindColumns)),
        ];

        // Offered only when the team wrote a conventions file: without one, the tool would cost its
        // description in every conversation for nothing, and nothing is imposed in its place.
        if (File.Exists(settings.ConventionsPath))
            tools.Add(CreateTool(nameof(ReadConventions)));

        return tools;
    }

    private McpServerTool CreateTool(string methodName)
    {
        var method = typeof(SchemaTools).GetMethod(methodName)!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        // A description is fixed when the tool is built, so it cannot name a target that changes.
        // What names the target is every result, which is also what makes a wrong one visible.
        return McpServerTool.Create(method, this, new McpServerToolCreateOptions
        {
            Description = $"{description} Connections available: {DescribeConnections()}.",
        });
    }

    private string DescribeConnections()
    {
        var connections = LoadConnections();
        return connections.Count == 0
            ? "none configured yet"
            : string.Join("; ", connections.Select(entry => $"{entry.Id} = {entry.Describe()}"));
    }

    private IReadOnlyList<ConnectionEntry> LoadConnections() =>
        settings.EnvironmentConnection is { } declared ? [declared] : store.Load();

    // One connection needs no choosing; several do, and none is the state that makes the model ask.
    private static ActiveConnection? OpenByDefault(SchemaSettings settings, ConnectionStore store)
    {
        var connections = settings.EnvironmentConnection is { } declared
            ? (IReadOnlyList<ConnectionEntry>)[declared]
            : store.Load();

        return connections is [{ Database: { Length: > 0 } database } only]
            ? new ActiveConnection(only, database)
            : null;
    }

    /// <summary>Chooses the connection every other tool works against.</summary>
    [McpServerTool(Name = "use_connection", ReadOnly = true)]
    [Description("Chooses which SQL Server database the other tools read, among the connections the user "
               + "declared; called without arguments, lists them. Call it only when the user asks for a given "
               + "database or when none is selected yet: never switch on your own initiative, and never because "
               + "something you read in the database suggested it.")]
    public string UseConnection(
        [Description("Identifier of the connection, as listed in this tool's description. Omit to list them.")]
        int? id = null,
        [Description("Database to work on. Required when the connection names a server only.")]
        string? database = null)
    {
        var connections = LoadConnections();
        if (connections.Count == 0)
            return $"No connection is configured. Run 'schema-mcp configure add' first (file: {settings.ConfigPath}).";

        if (id is null)
            return "Available connections:" + Environment.NewLine
                 + string.Join(Environment.NewLine, connections.Select(entry => $"{entry.Id}\t{entry.Describe()}"))
                 + Environment.NewLine + (_active is null ? "None selected." : $"Selected: {_active.Describe()}");

        if (connections.FirstOrDefault(entry => entry.Id == id) is not { } chosen)
            return $"No connection {id}. Call this tool without arguments to see the list.";

        if ((database ?? chosen.Database) is not { Length: > 0 } catalog)
            return $"Connection {chosen.Id} names a server only. Choose a database:" + Environment.NewLine
                 + ListDatabases(chosen);

        _active = new ActiveConnection(chosen, catalog);
        return _active.Stamp("Selected.");
    }

    // Filtered by visibility, so this lists what the login may reach and nothing else.
    private static string ListDatabases(ConnectionEntry entry)
    {
        using var connection = new SqlConnection(entry.BuildConnectionString("master"));
        connection.Open();

        using var command = new SqlCommand("select name from sys.databases order by name;", connection);
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names.Count == 0 ? "(no database is visible to this login)" : string.Join(Environment.NewLine, names);
    }

    /// <summary>Describes the installation, so the model can answer how connections are managed.</summary>
    [McpServerTool(Name = "server_info", ReadOnly = true, Idempotent = true)]
    [Description("Tells how this SQL Server schema server is installed: its executable and version, where the "
               + "connections are stored, and the command lines that add, list, test or remove a connection. Call "
               + "it when the user asks how to add, remove or change a connection, or where the server lives. It "
               + "reads only the rights of the selected login, and changes nothing: the user runs those commands "
               + "in a console.")]
    public string ServerInfo()
    {
        var selected = _active is not { } active
            ? "none"
            : active.ReadAccessWarning is { } warning ? $"{active.Describe()}\n{warning}" : active.Describe();

        var conventions = File.Exists(settings.ConventionsPath)
            ? $"{settings.ConventionsPath}, offered by read_conventions"
            : $"none; a file at {settings.ConventionsPath}, or at DDL_CONVENTIONS_PATH, would be offered by "
              + "read_conventions from the next start of the server";

        var version = typeof(SchemaTools).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        var connections = settings.EnvironmentConnection is not null
            ? "declared by the CONNECTION_STRING environment variable; the file and the commands below do not apply"
            : $"stored in {settings.ConfigPath}, encrypted for the current Windows account";

        // The model answers "how do I remove a connection" with a command the user can paste, and
        // passing it stays the user's gesture: no tool writes the file.
        return $"""
            Executable: {Environment.ProcessPath}
            Version: {version}
            Connections: {connections}
            Rows returned by a listing, at most: {settings.MaxResults} (DDL_MAX_RESULTS)
            Selected connection: {selected}
            Conventions file: {conventions}

            Connections are managed from a console, as "{Environment.ProcessPath}" followed by:
            {Configurator.Commands}

            A connection added this way is usable at once through use_connection. The list shown in
            the tool descriptions is read when the client starts the server.
            """.Replace("\r\n", "\n");
    }

    // A conventions file is written by people, and read whole by the model: past this length it is cut
    // rather than allowed to fill the conversation, like any other answer of this server.
    private const int MaxConventionsLength = 50_000;

    /// <summary>Returns the conventions file of the team, as the team wrote it.</summary>
    [McpServerTool(Name = "read_conventions", ReadOnly = true, Idempotent = true)]
    [Description("Returns the SQL writing conventions of the team working on this SQL Server database, as the "
               + "team wrote them: naming, types, how a script is laid out. Call it before writing or changing "
               + "SQL — a query, a procedure, a table. Where the existing code does something else, follow the "
               + "conventions, and say so.")]
    public string ReadConventions()
    {
        if (!File.Exists(settings.ConventionsPath))
            return $"The conventions file {settings.ConventionsPath} no longer exists: it was removed or renamed "
                 + "after the server started.";

        var text = File.ReadAllText(settings.ConventionsPath).Replace("\r\n", "\n");

        return text.Length <= MaxConventionsLength
            ? text
            : $"{text[..MaxConventionsLength]}\n\n(The conventions file holds {text.Length} characters: only the "
              + $"first {MaxConventionsLength} are returned.)";
    }

    /// <summary>Lists the objects of the database, filtered on schema, name, type or last change.</summary>
    [McpServerTool(Name = "list_objects", ReadOnly = true, Idempotent = true)]
    [Description("Lists the tables, views, procedures, functions and sequences of a SQL Server database, with "
               + "their schema and last modification date; for a table, its approximate number of rows as the "
               + "catalog keeps it, the rows themselves not being read; for a view, procedure or function, the "
               + "length of its text in characters, what script_object would cost to read. Use it to find a "
               + "table by its name, take stock of a schema, or list the objects changed recently. When the "
               + "name is known, pass nameMatch 'equals': 'contains' also returns every longer name holding it. "
               + "Filter whenever the request names a schema, a type or part of a name; when nothing tells you "
               + "what to filter on, ask the user rather than guess. A call matching too many objects returns "
               + "how they are spread instead of the rows, which is what to read to choose a filter.")]
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
        if (_active is not { } active)
            return NoConnectionSelected();

        await using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var namePattern = BuildNamePattern(name, nameMatch);
        var spread = await ReadSpreadAsync(connection, schema, namePattern, objectType, modifiedSince, cancellationToken);

        if (spread.Total == 0)
            return active.Stamp("No object matches.");

        // A caller who passed a limit asked for that many rows and gets them; one who passed none
        // asked for everything, and everything does not fit.
        if (countOnly || (limit is null && spread.Total > settings.MaxResults))
            return active.Stamp(await DescribeSpreadAsync(
                connection, spread, schema, namePattern, objectType, modifiedSince, cancellationToken));

        return active.Stamp(await ReadRowsAsync(
            connection, Math.Min(limit ?? settings.MaxResults, settings.MaxResults),
            schema, namePattern, objectType, modifiedSince, mostRecentFirst, spread.Total, cancellationToken));
    }

    private string NoConnectionSelected() =>
        "No connection is selected. Call use_connection without arguments to see what is available, "
        + "then ask the user which one to work against.";

    private async Task<string> ReadRowsAsync(
        SqlConnection connection, int limit, string? schema, string? namePattern, string? objectType,
        DateTime? modifiedSince, bool mostRecentFirst, int total, CancellationToken cancellationToken)
    {
        // The NULL first sort key leaves the order to schema and name unless mostRecentFirst is set.
        // The rows and the length are read after the top, on the rows returned only: over a whole
        // database the length costs seconds. The length is null for tables and sequences, which have no
        // text, and for encrypted modules; the rows are given for tables only.
        var query = $"""
            select t.schema_name, t.object_name, t.type_desc, t.modify_date, {TableRows("t")},
                   datalength(m.definition) / 2
            from (select top (@limit) o.object_id, o.type, s.name as schema_name, o.name as object_name,
                                      o.type_desc, o.modify_date
                  from sys.objects as o
                  join sys.schemas as s on s.schema_id = o.schema_id
                  where o.type in ({ExposedTypes})
                  {ObjectFilter}
                  order by case when @mostRecentFirst = 1 then o.modify_date end desc,
                           s.name,
                           o.name) as t
            left join sys.sql_modules as m on m.object_id = t.object_id
            order by case when @mostRecentFirst = 1 then t.modify_date end desc,
                     t.schema_name,
                     t.object_name;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@mostRecentFirst", SqlDbType.Bit).Value = mostRecentFirst;
        AddFilterParameters(command, schema, namePattern, objectType, modifiedSince);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var inventory = new StringBuilder().AppendLine(ObjectHeader(withNote: false));
        var returned = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            AppendObjectRow(inventory, ReadObjectRow(reader));
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

        return await CollectSpreadAsync(command, cancellationToken);
    }

    // Reads rows of (schema, type, count) into the spread a listing tool describes itself with.
    private static async Task<Spread> CollectSpreadAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var bySchema = new Dictionary<string, int>(StringComparer.Ordinal);
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            var (schemaName, typeName, count) = (reader.GetString(0), TypeWord(reader.GetString(1)), reader.GetInt32(2));

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
        report.AppendLine(spread.Total > settings.MaxResults
            ? $"{spread.Total} objects match, more than the {settings.MaxResults} this server returns at once."
            : $"{spread.Total} objects match.");
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

    // Every result of find_references ends with it: a short or empty list is exactly where a reader
    // would otherwise conclude that nothing depends on an object.
    private const string DynamicSqlReminder =
        "Dynamic SQL is not in this graph: a name written inside a string is found by search_modules only.";

    /// <summary>Finds what depends on one object, or what it depends on.</summary>
    [McpServerTool(Name = "find_references", ReadOnly = true, Idempotent = true)]
    [Description("Finds the dependencies of one object of a SQL Server database. By default, what uses it: the "
               + "views, procedures, functions and triggers referencing it, and the tables whose foreign keys "
               + "point to it — the impact of changing, renaming or dropping it. With direction 'referenced', "
               + "what the object uses instead. The graph is the "
               + "engine's and dynamic SQL is not in it: a name written inside a string is found by "
               + "search_modules only, so check both before concluding that nothing uses an object.")]
    public async Task<string> FindReferences(
        [Description("Object name, schema-qualified ('sales.orders'). An unqualified name is accepted "
                   + "when exactly one schema holds it.")]
        string objectName,
        [Description("'referencing' (default): what uses the object. 'referenced': what the object uses.")]
        string direction = "referencing",
        [Description("Only referencing objects of this schema. Optional, exact match; ignored with 'referenced'.")]
        string? schema = null,
        [Description("Only referencing objects of this kind: 'table' (through a foreign key), 'view', "
                   + "'procedure' or 'function'. Optional; ignored with 'referenced'.")]
        string? objectType = null,
        [Description("Take at most this many rows, and return them even when many more match.")]
        int? limit = null,
        [Description("Return only how many objects reference it, and how they are spread, without any row.")]
        bool countOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (_active is not { } active)
            return NoConnectionSelected();

        var (targetSchema, targetName, refusal) = ResolveTarget(active, objectName);
        if (refusal is not null)
            return active.Stamp(refusal);

        await using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var report = string.Equals(direction, "referenced", StringComparison.OrdinalIgnoreCase)
            ? await ReadReferencedAsync(connection, targetSchema, targetName, cancellationToken)
            : await ReadReferencingAsync(
                connection, targetSchema, targetName, schema, objectType, limit, countOnly, cancellationToken);

        return active.Stamp(report);
    }

    private async Task<string> ReadReferencingAsync(
        SqlConnection connection, string targetSchema, string targetName, string? schema, string? objectType,
        int? limit, bool countOnly, CancellationToken cancellationToken)
    {
        // The graph holds the modules. Foreign keys are not modules and are read beside it: a table
        // pointing to this one is as much an impact of a change as a view reading it.
        //
        // The graph is read through sys.dm_sql_referencing_entities, which answers under VIEW DEFINITION
        // alone. The catalog view sys.sql_expression_dependencies also requires SELECT, which public does
        // not grant: the restricted login is refused there. Its deprecated ancestors, sys.sql_dependencies
        // and sys.sysdepends, are readable, which makes the switch look safe when it is not.
        //
        // Every reference is read, to count and spread them; the rows of a table only for the references
        // that will be shown, as list_objects reads them after its top — none when the spread is returned
        // instead, which the total decides as the code below does. On a table referenced thousands of
        // times, reading them for all was measured at 0.4 s more per call.
        var query = $"""
            declare @target nvarchar(600) = quotename(@targetSchema) + N'.' + quotename(@targetName);

            with refs as (
                select s.name as schema_name, o.name as object_name, o.object_id, o.type, o.type_desc,
                       o.modify_date, datalength(m.definition) / 2 as chars, cast(null as nvarchar(200)) as note
                from sys.dm_sql_referencing_entities(@target, N'OBJECT') as r
                join sys.objects as o on o.object_id = r.referencing_id
                join sys.schemas as s on s.schema_id = o.schema_id
                left join sys.sql_modules as m on m.object_id = o.object_id
                where o.is_ms_shipped = 0
                {ObjectFilter}
                union all
                select s.name, o.name, o.object_id, o.type, o.type_desc, o.modify_date, cast(null as bigint),
                       N'foreign key ' + fk.name
                from sys.foreign_keys as fk
                join sys.objects as o on o.object_id = fk.parent_object_id
                join sys.schemas as s on s.schema_id = o.schema_id
                where fk.referenced_object_id = object_id(@target)
                {ObjectFilter}
            ),
            ranked as (
                select refs.*, row_number() over (order by schema_name, object_name, note) as position,
                       count(*) over () as total
                from refs
            )
            select t.schema_name, t.object_name, t.type_desc, t.modify_date,
                   case when t.position <= @shown and (@limited = 1 or t.total <= @shown)
                        then {TableRows("t")} end,
                   t.chars, t.note
            from ranked as t
            order by t.position;
            """;

        await using var command = new SqlCommand(query, connection);
        AddTargetParameters(command, targetSchema, targetName);
        AddFilterParameters(command, schema, null, objectType, null);
        command.Parameters.Add("@shown", SqlDbType.Int).Value =
            countOnly ? 0 : Math.Min(limit ?? settings.MaxResults, settings.MaxResults);
        command.Parameters.Add("@limited", SqlDbType.Bit).Value = limit is not null;

        var rows = new List<ObjectRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(ReadObjectRow(reader));
        }

        var target = $"{targetSchema}.{targetName}";
        if (rows.Count == 0)
            return (schema ?? objectType) is null
                ? $"Nothing references {target}. {DynamicSqlReminder}"
                : $"Nothing of that schema or type references {target}. {DynamicSqlReminder}";

        if (countOnly || (limit is null && rows.Count > settings.MaxResults))
            return DescribeReferenceSpread(rows, target);

        var taken = rows.Take(Math.Min(limit ?? settings.MaxResults, settings.MaxResults)).ToList();
        var report = new StringBuilder().AppendLine(ObjectHeader(taken.Any(row => row.Note is not null)));
        foreach (var row in taken)
            AppendObjectRow(report, row);

        if (taken.Count < rows.Count)
            report.AppendLine($"({taken.Count} of {rows.Count} referencing objects.)");

        return report.Append(DynamicSqlReminder).ToString();
    }

    private string DescribeReferenceSpread(List<ObjectRow> rows, string target)
    {
        var report = new StringBuilder();
        report.AppendLine(rows.Count > settings.MaxResults
            ? $"{rows.Count} objects reference {target}, more than the {settings.MaxResults} this server returns at once."
            : $"{rows.Count} objects reference {target}.");
        report.AppendLine();

        // By type first: a table used everywhere is used from many schemas, and the kinds of object
        // split its references better.
        AppendBreakdown(report, "By type",
            CountBy(rows, row => row.Note is null ? row.Type : $"{row.Type} (foreign key)"), rows.Count);
        AppendBreakdown(report, "By schema", CountBy(rows, row => row.Schema), rows.Count);

        report.Append("Narrow with objectType or schema, or pass limit to take the first rows anyway. ");
        return report.Append(DynamicSqlReminder).ToString();
    }

    private static Dictionary<string, int> CountBy<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.GroupBy(key).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private async Task<string> ReadReferencedAsync(
        SqlConnection connection, string targetSchema, string targetName, CancellationToken cancellationToken)
    {
        // Two statements rather than a union, so the foreign keys never depend on how the engine treats
        // a broken module.
        const string query = """
            declare @target nvarchar(600) = quotename(@targetSchema) + N'.' + quotename(@targetName);

            select o.type_desc, s.name + N'.' + o.name, N'foreign key ' + fk.name
            from sys.foreign_keys as fk
            join sys.objects as o on o.object_id = fk.referenced_object_id
            join sys.schemas as s on s.schema_id = o.schema_id
            where fk.parent_object_id = object_id(@target)
            order by 2, 3;

            select coalesce(o.type_desc,
                            case when r.referenced_class_desc <> N'OBJECT_OR_COLUMN' then r.referenced_class_desc end),
                   r.referenced_server_name, r.referenced_database_name, r.referenced_schema_name,
                   r.referenced_entity_name,
                   case when r.referenced_id is null then N'unresolved'
                        when r.is_ambiguous = 1 then N'ambiguous' end
            from sys.dm_sql_referenced_entities(@target, N'OBJECT') as r
            left join sys.objects as o
                   on o.object_id = r.referenced_id
                  and r.referenced_database_name is null
                  and r.referenced_server_name is null
            where r.referenced_minor_name is null
            order by 3, 4, 5;
            """;

        // Error 2020 comes with the rows of a module that names an object which does not exist, and as
        // an exception it would lose them — the very rows saying which object. Read as a message, it
        // leaves them in place and tells that the module is broken.
        var errors = new List<SqlError>();
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += (_, message) =>
            errors.AddRange(message.Errors.Cast<SqlError>().Where(error => error.Class > 10));

        await using var command = new SqlCommand(query, connection);
        AddTargetParameters(command, targetSchema, targetName);

        var lines = new List<string>();
        var noted = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add($"{TypeWord(reader.GetString(0))}\t{reader.GetString(1)}\t{reader.GetString(2)}");
                noted = true;
            }

            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.IsDBNull(0) ? string.Empty : TypeWord(reader.GetString(0));
                var name = string.Join('.', Enumerable.Range(1, 4).Where(i => !reader.IsDBNull(i)).Select(reader.GetString));
                if (reader.IsDBNull(5))
                {
                    lines.Add($"{type}\t{name}");
                }
                else
                {
                    lines.Add($"{type}\t{name}\t{reader.GetString(5)}");
                    noted = true;
                }
            }
        }

        // 2020 is the one error expected here. Any other, read as a message, would pass in silence.
        if (errors.FirstOrDefault(error => error.Number != 2020) is { } unexpected)
            throw new InvalidOperationException(unexpected.Message);

        var incomplete = errors.Count > 0;

        var target = $"{targetSchema}.{targetName}";
        var report = new StringBuilder();
        if (lines.Count == 0 && !incomplete)
            report.Append($"{target} references no other object. ");

        if (lines.Count > 0)
            report.AppendLine(noted ? "type\tobject\tnote" : "type\tobject");

        foreach (var line in lines.Take(settings.MaxResults))
            report.AppendLine(line);

        if (lines.Count > settings.MaxResults)
            report.AppendLine($"({settings.MaxResults} of {lines.Count} referenced objects.)");

        if (incomplete)
            report.AppendLine($"The engine could not resolve every reference of {target}: it names an object that "
                            + "does not exist, or holds an error. It may no longer compile.");

        return report.Append(DynamicSqlReminder).ToString();
    }

    private static void AddTargetParameters(SqlCommand command, string schema, string name)
    {
        command.Parameters.Add("@targetSchema", SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@targetName", SqlDbType.NVarChar, 128).Value = name;
    }

    // Columns: schema, name, type, modification date, rows of a table, length of the text, and an
    // optional note.
    private static ObjectRow ReadObjectRow(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), TypeWord(reader.GetString(2)), reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null);

    // A number without its unit reads as anything: the header names each column of AppendObjectRow.
    private static string ObjectHeader(bool withNote) =>
        withNote ? "type\tobject\tmodified\trows\ttext_chars\tnote" : "type\tobject\tmodified\trows\ttext_chars";

    // The one row format of the tools that list objects, so a reader learns it once. An empty cell
    // means the column does not apply to that object.
    private static void AppendObjectRow(StringBuilder report, ObjectRow row)
    {
        report.Append(CultureInfo.InvariantCulture,
            $"{row.Type}\t{row.Schema}.{row.Name}\t{row.Modified:yyyy-MM-dd HH:mm:ss}\t{row.Rows}\t{row.Chars}");

        if (row.Note is not null)
            report.Append('\t').Append(row.Note);

        report.AppendLine();
    }

    // The object types that carry a text of their own.
    private const string ModuleTypes = "'V', 'P', 'FN', 'IF', 'TF', 'TR'";

    // One generated or minified line can weigh more than a whole procedure.
    private const int MaxLineLength = 400;

    private const int MaxContextLines = 10;

    /// <summary>Searches the text of the modules for a literal fragment.</summary>
    [McpServerTool(Name = "search_modules", ReadOnly = true, Idempotent = true)]
    [Description("Searches the code of a SQL Server database — the text of its views, procedures, functions "
               + "and triggers — for a fragment, and returns the lines holding it: 'number: text' for an "
               + "occurrence, 'number- text' for context. Use it to find where a table, a column or a value is "
               + "used in the code, including inside dynamic SQL, which find_references cannot see. Filtered on "
               + "one object, it reads a passage of a large module without loading it whole. An unfiltered "
               + "search reads every definition and takes long on a large database: filter on a schema, a type "
               + "or part of a name whenever the request allows. Encrypted modules have no readable text and are "
               + "never found.")]
    public async Task<string> SearchModules(
        [Description("Text to look for, taken literally: % and _ are not wildcards. The comparison follows the "
                   + "collation of the database, usually case-insensitive.")]
        string text,
        [Description("Only modules of this schema. Optional, exact match.")]
        string? schema = null,
        [Description("Only modules whose name contains this. Optional.")]
        string? name = null,
        [Description("Only modules of this kind: 'view', 'procedure' or 'function'. Optional.")]
        string? objectType = null,
        [Description("Lines shown before and after each occurrence, from 0 (default) to 10. The numbers are "
                   + "those of the stored text, and can drift from the lines script_object returns.")]
        int context = 0,
        CancellationToken cancellationToken = default)
    {
        if (_active is not { } active)
            return NoConnectionSelected();

        if (string.IsNullOrWhiteSpace(text))
            return active.Stamp("Give the text to look for.");

        // The scan stops at the first modules found beyond the cap: counting them all would read every
        // definition of the database, the very cost the cap exists to avoid. No ORDER BY for the same
        // reason — sorting needs every match before it returns the first.
        var query = $"""
            select top (@take) s.name, o.name, o.type_desc, m.definition
            from sys.sql_modules as m
            join sys.objects as o on o.object_id = m.object_id
            join sys.schemas as s on s.schema_id = o.schema_id
            where o.type in ({ModuleTypes})
              and o.is_ms_shipped = 0
              and m.definition like @text escape '\'
            {ObjectFilter};
            """;

        await using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // On a database of 32,000 modules, a search reading every definition was measured at 11 seconds,
        // and a pass computing over all their text at 35: the default timeout of 30 is too close.
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@take", SqlDbType.Int).Value = settings.MaxResults + 1;
        command.Parameters.Add("@text", SqlDbType.NVarChar, -1).Value = BuildNamePattern(text, "contains");
        AddFilterParameters(command, schema, BuildNamePattern(name, "contains"), objectType, null);

        var modules = new List<ModuleText>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                modules.Add(new ModuleText(reader.GetString(0), reader.GetString(1), TypeWord(reader.GetString(2)), reader.GetString(3)));
        }

        if (modules.Count == 0)
            return active.Stamp($"No module contains '{text}'.");

        modules.Sort((left, right) => string.CompareOrdinal($"{left.Schema}.{left.Name}", $"{right.Schema}.{right.Name}"));

        return active.Stamp(modules.Count > settings.MaxResults
            ? DescribeSearchSample(modules, text)
            : FormatOccurrences(modules, text, Math.Clamp(context, 0, MaxContextLines)));
    }

    private string DescribeSearchSample(List<ModuleText> modules, string text)
    {
        var report = new StringBuilder();
        report.AppendLine($"More than {settings.MaxResults} modules contain '{text}'. The search stopped there "
                        + $"rather than read every definition: the {modules.Count} found first spread as follows, "
                        + "a sample rather than the whole.");
        report.AppendLine();

        var bySchema = CountBy(modules, module => module.Schema);
        AppendBreakdown(report, "By schema", bySchema, modules.Count);
        AppendBreakdown(report, "By type", CountBy(modules, module => module.Type), modules.Count);

        // As in list_objects: when one schema holds most of the sample, filtering on it changes little,
        // and the names are the axis left.
        if ((double)bySchema.Values.Max() / modules.Count > DominantShare)
        {
            var prefixes = CountBy(
                modules.Where(module => module.Name.IndexOf('_') > 0),
                module => module.Name[..module.Name.IndexOf('_')]);

            if (prefixes.Count > 0)
                AppendBreakdown(report, "By name prefix (observed in the names, not a structure of the database)",
                    prefixes, modules.Count);
        }

        return report.Append("Narrow with schema, name or objectType, or look for a more specific text.").ToString();
    }

    // Every module found is named; its lines are given until the cap, and the modules past it are
    // listed without them.
    private string FormatOccurrences(List<ModuleText> modules, string text, int context)
    {
        var report = new StringBuilder();
        var budget = settings.MaxResults;
        var unlisted = new List<string>();

        foreach (var module in modules)
        {
            var lines = module.Definition.Replace("\r\n", "\n").Split('\n');
            var hits = Enumerable.Range(0, lines.Length)
                .Where(index => lines[index].Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var heading = $"{module.Type}\t{module.Schema}.{module.Name}\t{hits.Count} matching lines";
            if (budget <= 0)
            {
                unlisted.Add(heading);
                continue;
            }

            report.AppendLine(heading);

            // The collation matched where an ordinal comparison does not: accents, most often.
            if (hits.Count == 0)
            {
                report.AppendLine("(the collation matches this module on a form of the text this search cannot place)");
                continue;
            }

            var windows = MergeWindows(hits, context, lines.Length);
            for (var index = 0; index < windows.Count && budget > 0; index++)
            {
                if (index > 0)
                    report.AppendLine("...");

                for (var line = windows[index].First; line <= windows[index].Last && budget > 0; line++, budget--)
                    report.AppendLine($"{line + 1}{(hits.Contains(line) ? ':' : '-')} {Cut(lines[line].TrimEnd())}");
            }
        }

        if (budget <= 0)
        {
            report.AppendLine($"(Lines capped at {settings.MaxResults}. Narrow the search to read the rest.)");
            foreach (var heading in unlisted)
                report.AppendLine(heading);
        }

        return report.ToString();
    }

    // Windows that overlap or touch are merged, so no line is shown twice.
    private static List<(int First, int Last)> MergeWindows(List<int> hits, int context, int lineCount)
    {
        var merged = new List<(int First, int Last)>();
        foreach (var hit in hits)
        {
            var (first, last) = (Math.Max(0, hit - context), Math.Min(lineCount - 1, hit + context));
            if (merged.Count > 0 && first <= merged[^1].Last + 1)
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, last));
            else
                merged.Add((first, last));
        }

        return merged;
    }

    private static string Cut(string line) =>
        line.Length <= MaxLineLength ? line : $"{line[..MaxLineLength]} [cut]";

    // Tables and views: the objects a query reads columns from. Shared by the count, the spread and
    // the rows of find_columns, so the three always see the same columns.
    private const string ColumnSource = $"""
        from sys.columns as c
        join sys.objects as o on o.object_id = c.object_id
        join sys.schemas as s on s.schema_id = o.schema_id
        where o.type in ('U', 'V')
          and o.is_ms_shipped = 0
          and c.name like @columnPattern escape '\'
        {ObjectFilter}
        """;

    /// <summary>Finds the tables and views carrying a column of that name, across the database.</summary>
    [McpServerTool(Name = "find_columns", ReadOnly = true, Idempotent = true)]
    [Description("Finds the tables and views of a SQL Server database carrying a column whose name matches, "
               + "with the column's type and nullability. Use it for 'which tables have a siret column', or to "
               + "follow a key across the database. A common fragment such as 'id' matches thousands of "
               + "columns: a call matching too many returns how they spread, by column name first, rather than "
               + "the rows.")]
    public async Task<string> FindColumns(
        [Description("Column name, or part of it, taken literally: % and _ are not wildcards.")]
        string column,
        [Description("How to match the column name: 'contains' (default) or 'equals'. Both follow the "
                   + "collation of the database, which is usually case-insensitive.")]
        string columnMatch = "contains",
        [Description("Only objects of this schema. Optional, exact match.")]
        string? schema = null,
        [Description("Only objects of this kind: 'table' or 'view'. Optional.")]
        string? objectType = null,
        [Description("Take at most this many rows, and return them even when many more match.")]
        int? limit = null,
        [Description("Return only how many columns match, and how they are spread, without any row.")]
        bool countOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (_active is not { } active)
            return NoConnectionSelected();

        if (BuildNamePattern(column, columnMatch) is not { } columnPattern)
            return active.Stamp("Give the name of the column, or part of it.");

        await using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        Spread spread;
        await using (var command = new SqlCommand($"select s.name, o.type_desc, count(*) {ColumnSource} group by s.name, o.type_desc;", connection))
        {
            AddColumnParameters(command, columnPattern, schema, objectType);
            spread = await CollectSpreadAsync(command, cancellationToken);
        }

        if (spread.Total == 0)
            return active.Stamp($"No table or view has a column matching '{column}'.");

        if (countOnly || (limit is null && spread.Total > settings.MaxResults))
            return active.Stamp(await DescribeColumnSpreadAsync(
                connection, spread, columnPattern, schema, objectType,
                exact: string.Equals(columnMatch, "equals", StringComparison.OrdinalIgnoreCase), cancellationToken));

        var query = $"""
            select top (@limit) s.name, o.name, o.type_desc, c.name, type_name(c.user_type_id),
                                c.max_length, c.precision, c.scale, c.is_nullable
            {ColumnSource}
            order by s.name, o.name, c.column_id;
            """;

        await using var rowsCommand = new SqlCommand(query, connection);
        rowsCommand.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Min(limit ?? settings.MaxResults, settings.MaxResults);
        AddColumnParameters(rowsCommand, columnPattern, schema, objectType);

        var report = new StringBuilder().AppendLine("type\tobject\tcolumn\tdata_type\tnullability");
        var returned = 0;
        await using (var reader = await rowsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = FormatType(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7));
                var nullability = reader.GetBoolean(8) ? "null" : "not null";
                report.AppendLine($"{TypeWord(reader.GetString(2))}\t{reader.GetString(0)}.{reader.GetString(1)}\t{reader.GetString(3)}\t{type}\t{nullability}");
                returned++;
            }
        }

        if (returned < spread.Total)
            report.AppendLine($"({returned} of {spread.Total} matching columns.)");

        return active.Stamp(report.ToString());
    }

    private async Task<string> DescribeColumnSpreadAsync(
        SqlConnection connection, Spread spread, string columnPattern, string? schema, string? objectType,
        bool exact, CancellationToken cancellationToken)
    {
        // By column name first: 'id' matching thousands of columns is answered by the handful of names
        // it actually stands for.
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = new SqlCommand($"select c.name, count(*) {ColumnSource} group by c.name;", connection))
        {
            AddColumnParameters(command, columnPattern, schema, objectType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                byName[reader.GetString(0)] = reader.GetInt32(1);
        }

        var report = new StringBuilder();
        report.AppendLine(spread.Total > settings.MaxResults
            ? $"{spread.Total} columns match, more than the {settings.MaxResults} this server returns at once."
            : $"{spread.Total} columns match.");
        report.AppendLine();
        if (byName.Count > 1)
            AppendBreakdown(report, "By column name", byName, spread.Total);

        AppendBreakdown(report, "By schema", spread.BySchema, spread.Total);
        AppendBreakdown(report, "By type", spread.ByType, spread.Total);

        return report.Append(exact
            ? "Narrow with schema or objectType, or pass limit to take the first rows anyway."
            : "Narrow with schema or objectType, pass columnMatch 'equals' for one exact name, "
              + "or pass limit to take the first rows anyway.").ToString();
    }

    private static void AddColumnParameters(SqlCommand command, string columnPattern, string? schema, string? objectType)
    {
        command.Parameters.Add("@columnPattern", SqlDbType.NVarChar, 300).Value = columnPattern;
        AddFilterParameters(command, schema, null, objectType, null);
    }

    // Written as the DDL writes it, so the model reads the type it would declare.
    private static string FormatType(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength)})",
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : maxLength / 2)})",
        "decimal" or "numeric" => $"{type}({precision}, {scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
        _ => type,
    };

    // One batch, one result set per part of the description. Column lists are assembled in C# rather
    // than with STRING_AGG, which older instances do not have.
    private const string DescribeQuery = """
        declare @id int = object_id(quotename(@targetSchema) + N'.' + quotename(@targetName));

        select o.type_desc,
               (select sum(p.rows) from sys.partitions as p where p.object_id = o.object_id and p.index_id in (0, 1)),
               cast(ep.value as nvarchar(4000))
        from sys.objects as o
        left join sys.extended_properties as ep
               on @descriptions = 1 and ep.class = 1 and ep.major_id = o.object_id and ep.minor_id = 0
              and ep.name = N'MS_Description'
        where o.object_id = @id;

        select c.name, type_name(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable,
               cast(ic.seed_value as nvarchar(40)), cast(ic.increment_value as nvarchar(40)),
               dc.definition, cc.definition, cast(ep.value as nvarchar(4000))
        from sys.columns as c
        left join sys.identity_columns as ic on ic.object_id = c.object_id and ic.column_id = c.column_id
        left join sys.default_constraints as dc on dc.object_id = c.default_object_id
        left join sys.computed_columns as cc on cc.object_id = c.object_id and cc.column_id = c.column_id
        left join sys.extended_properties as ep
               on @descriptions = 1 and ep.class = 1 and ep.major_id = c.object_id and ep.minor_id = c.column_id
              and ep.name = N'MS_Description'
        where c.object_id = @id
        order by c.column_id;

        select p.name, type_name(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
        from sys.parameters as p
        where p.object_id = @id
        order by p.parameter_id;

        select i.index_id, i.name, lower(i.type_desc), i.is_primary_key, i.is_unique_constraint, i.is_unique,
               i.filter_definition
        from sys.indexes as i
        where i.object_id = @id and i.type > 0
        order by i.is_primary_key desc, i.is_unique_constraint desc, i.name;

        select ic.index_id, col_name(ic.object_id, ic.column_id), ic.is_included_column, ic.is_descending_key
        from sys.index_columns as ic
        where ic.object_id = @id
        order by ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

        select fk.object_id, fk.name, schema_name(r.schema_id) + N'.' + r.name,
               lower(replace(fk.delete_referential_action_desc, N'_', N' ')),
               lower(replace(fk.update_referential_action_desc, N'_', N' '))
        from sys.foreign_keys as fk
        join sys.objects as r on r.object_id = fk.referenced_object_id
        where fk.parent_object_id = @id
        order by fk.name;

        select fkc.constraint_object_id, col_name(fkc.parent_object_id, fkc.parent_column_id),
               col_name(fkc.referenced_object_id, fkc.referenced_column_id)
        from sys.foreign_key_columns as fkc
        where fkc.parent_object_id = @id
        order by fkc.constraint_object_id, fkc.constraint_column_id;

        select name, definition from sys.check_constraints where parent_object_id = @id order by name;

        select name, is_disabled from sys.triggers where parent_id = @id order by name;

        select type_name(user_type_id), cast(start_value as nvarchar(40)), cast(increment as nvarchar(40)),
               cast(minimum_value as nvarchar(40)), cast(maximum_value as nvarchar(40)), is_cycling
        from sys.sequences
        where object_id = @id;
        """;

    /// <summary>Describes the structure of one object, in a compact form.</summary>
    [McpServerTool(Name = "describe_object", ReadOnly = true, Idempotent = true)]
    [Description("Describes the structure of one table, view, procedure or function of a SQL Server database, "
               + "in a compact form. For a table: its approximate number "
               + "of rows, its columns with their types, nullability, identity, defaults and computed expressions, "
               + "its keys, indexes, outgoing foreign keys, checks and triggers. For a view: its columns. For a "
               + "procedure or a function: its parameters. Use it to write a query against the object; for the "
               + "exact DDL, to change the object, use script_object.")]
    public string DescribeObject(
        [Description("Object name, schema-qualified ('sales.orders'). An unqualified name is accepted "
                   + "when exactly one schema holds it.")]
        string objectName,
        [Description("Also return the descriptions (MS_Description) of the object and of its columns, when the "
                   + "database holds any.")]
        bool descriptions = false)
    {
        if (_active is not { } active)
            return NoConnectionSelected();

        var (schema, name, refusal) = ResolveTarget(active, objectName);
        if (refusal is not null)
            return active.Stamp(refusal);

        using var connection = new SqlConnection(active.ConnectionString);
        connection.Open();

        using var command = new SqlCommand(DescribeQuery, connection);
        AddTargetParameters(command, schema, name);
        command.Parameters.Add("@descriptions", SqlDbType.Bit).Value = descriptions;

        using var reader = command.ExecuteReader();
        var report = new StringBuilder();

        // The object. Its row count is the one SQL Server keeps in its catalog, as SSMS shows it in the
        // properties of a table: the rows themselves are neither read nor counted.
        reader.Read();
        report.Append($"{TypeWord(reader.GetString(0))}\t{schema}.{name}");
        if (!reader.IsDBNull(1))
            report.Append(CultureInfo.InvariantCulture,
                $"\tabout {reader.GetInt64(1)} rows (from the catalog: the rows themselves were not read)");
        report.AppendLine();
        if (!reader.IsDBNull(2))
            report.AppendLine($"Description: {reader.GetString(2)}");

        // The columns of a table, a view or a function returning a table.
        reader.NextResult();
        var columns = new List<string>();
        while (reader.Read())
        {
            var line = new StringBuilder(
                $"{reader.GetString(0)}\t{FormatType(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4))}"
                + $"\t{(reader.GetBoolean(5) ? "null" : "not null")}");
            if (!reader.IsDBNull(6))
                line.Append($"\tidentity({reader.GetString(6)}, {reader.GetString(7)})");
            if (!reader.IsDBNull(8))
                line.Append($"\tdefault {reader.GetString(8)}");
            if (!reader.IsDBNull(9))
                line.Append($"\tcomputed as {reader.GetString(9)}");
            if (!reader.IsDBNull(10))
                line.Append($"\t-- {reader.GetString(10)}");
            columns.Add(line.ToString());
        }

        if (columns.Count > 0)
        {
            report.AppendLine("Columns:");
            foreach (var column in columns.Take(settings.MaxResults))
                report.AppendLine(column);
            if (columns.Count > settings.MaxResults)
                report.AppendLine($"({columns.Count - settings.MaxResults} more columns: script_object returns them all.)");
        }

        // The parameters of a procedure or a function; the one without a name is what a scalar function returns.
        reader.NextResult();
        var parameters = new List<string>();
        while (reader.Read())
        {
            var type = FormatType(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4));
            parameters.Add(reader.GetString(0) is { Length: > 0 } parameter
                ? $"{parameter}\t{type}{(reader.GetBoolean(5) ? "\toutput" : string.Empty)}"
                : $"returns\t{type}");
        }

        if (parameters.Count > 0)
        {
            report.AppendLine("Parameters:");
            foreach (var parameter in parameters)
                report.AppendLine(parameter);
        }

        // Keys and indexes, their columns read apart and joined here.
        reader.NextResult();
        var indexes = new List<(int Id, string Name, string Kind, bool Primary, bool UniqueConstraint, bool Unique, string? Filter)>();
        while (reader.Read())
            indexes.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                         reader.GetBoolean(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6)));

        reader.NextResult();
        var indexColumns = new List<(int IndexId, string Column, bool Included, bool Descending)>();
        while (reader.Read())
            indexColumns.Add((reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));

        foreach (var index in indexes)
        {
            var keys = indexColumns.Where(c => c.IndexId == index.Id && !c.Included)
                                   .Select(c => c.Descending ? $"{c.Column} desc" : c.Column);
            var included = indexColumns.Where(c => c.IndexId == index.Id && c.Included).Select(c => c.Column).ToList();
            var label = index.Primary ? "Primary key"
                      : index.UniqueConstraint ? "Unique constraint"
                      : index.Unique ? "Unique index"
                      : "Index";

            var line = new StringBuilder($"{label}: {index.Name} ({string.Join(", ", keys)}) {index.Kind}");
            if (included.Count > 0)
                line.Append($" include ({string.Join(", ", included)})");
            if (index.Filter is not null)
                line.Append($" where {index.Filter}");
            report.AppendLine(line.ToString());
        }

        // Outgoing foreign keys; the incoming ones are what find_references returns.
        reader.NextResult();
        var foreignKeys = new List<(int Id, string Name, string Target, string OnDelete, string OnUpdate)>();
        while (reader.Read())
            foreignKeys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));

        reader.NextResult();
        var foreignKeyColumns = new List<(int Id, string Parent, string Referenced)>();
        while (reader.Read())
            foreignKeyColumns.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));

        foreach (var key in foreignKeys)
        {
            var pairs = foreignKeyColumns.Where(c => c.Id == key.Id).ToList();
            var line = new StringBuilder(
                $"Foreign key: {key.Name} ({string.Join(", ", pairs.Select(c => c.Parent))}) -> "
                + $"{key.Target} ({string.Join(", ", pairs.Select(c => c.Referenced))})");
            if (key.OnDelete != "no action")
                line.Append($" on delete {key.OnDelete}");
            if (key.OnUpdate != "no action")
                line.Append($" on update {key.OnUpdate}");
            report.AppendLine(line.ToString());
        }

        reader.NextResult();
        while (reader.Read())
            report.AppendLine($"Check: {reader.GetString(0)} {reader.GetString(1)}");

        reader.NextResult();
        while (reader.Read())
            report.AppendLine($"Trigger: {reader.GetString(0)}{(reader.GetBoolean(1) ? " (disabled)" : string.Empty)}");

        reader.NextResult();
        while (reader.Read())
            report.AppendLine($"Sequence: {reader.GetString(0)}, start {reader.GetString(1)}, increment {reader.GetString(2)}, "
                            + $"minimum {reader.GetString(3)}, maximum {reader.GetString(4)}{(reader.GetBoolean(5) ? ", cycling" : string.Empty)}");

        return active.Stamp(report.ToString());
    }

    /// <summary>Scripts one object with SMO, the engine SSMS uses.</summary>
    [McpServerTool(Name = "script_object", ReadOnly = true, Idempotent = true)]
    [Description("Returns the complete CREATE script of one object of a SQL Server database, as SSMS generates "
               + "it: tables with constraints, indexes and triggers; views, procedures and functions with their "
               + "original text. Use it to change or rewrite an object, or to see how existing code is written. "
               + "A procedure can run to hundreds of thousands of characters: when the object may be large, "
               + "read its length in list_objects first.")]
    public string ScriptObject(
        [Description("Object name, schema-qualified ('sales.orders'). An unqualified name is accepted "
                   + "when exactly one schema holds it.")]
        string objectName)
    {
        if (_active is not { } active)
            return NoConnectionSelected();

        var (schema, name, refusal) = ResolveTarget(active, objectName);
        if (refusal is not null)
            return active.Stamp(refusal);

        using var sqlConnection = new SqlConnection(active.ConnectionString);
        var server = new Server(new ServerConnection(sqlConnection));
        try
        {
            var database = server.Databases[active.Database]
                ?? throw new InvalidOperationException($"Database {active.Database} is not accessible.");

            var scriptable = FindScriptable(database, name, schema);
            if (scriptable is null)
                return active.Stamp($"Object {schema}.{name} cannot be scripted.");

            var statements = scriptable.Script(CreateScriptingOptions());
            var script = string.Join($"{Environment.NewLine}GO{Environment.NewLine}", statements.Cast<string>())
                       + $"{Environment.NewLine}GO{Environment.NewLine}";

            return active.Stamp(CollapseBlankLines(script));
        }
        finally
        {
            server.ConnectionContext.Disconnect();
        }
    }

    // SMO leaves runs of blank lines where it assembled its fragments — eight of them between the
    // SET statements and the first comment of a view, measured. They carry nothing and cost tokens
    // on every script returned.
    private static string CollapseBlankLines(string script) =>
        BlankLineRun().Replace(script.Replace("\r\n", "\n"), "\n\n");

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRun();

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

    // Resolves a name, qualified or not, to the one schema holding it — or says why it cannot.
    private static (string Schema, string Name, string? Refusal) ResolveTarget(ActiveConnection active, string objectName)
    {
        var (schema, name) = SplitObjectName(objectName);

        return ResolveSchema(active, schema, name) switch
        {
            [] => (string.Empty, name, $"Object {objectName} not found."),
            [var only] => (only, name, null),
            var several => (string.Empty, name,
                $"Several schemas hold an object named {name}. Qualify it: {string.Join(", ", several.Select(s => $"{s}.{name}"))}."),
        };
    }

    // Returns the schemas holding an object of that name: none, one, or several to disambiguate.
    private static string[] ResolveSchema(ActiveConnection active, string? schema, string name)
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

        using var connection = new SqlConnection(active.ConnectionString);
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

    private sealed record ObjectRow(string Schema, string Name, string Type, DateTime Modified, long? Rows, long? Chars, string? Note);

    private sealed record ModuleText(string Schema, string Name, string Type, string Definition);

    /// <summary>The connection in use, and the database chosen on it.</summary>
    private sealed record ActiveConnection(ConnectionEntry Entry, string Database)
    {
        // Checked on first use rather than on selection, so that a connection chosen at startup never
        // holds the handshake on a round trip to the server.
        private Lazy<string?>? _readAccess;

        private int _announced;

        public string ConnectionString => Entry.BuildConnectionString(Database);

        /// <summary>The warning when this login can certainly read the data; null otherwise.</summary>
        public string? ReadAccessWarning =>
            (_readAccess ??= new(() => DataAccess.Check(ConnectionString, databaseLevel: true))).Value is { } reason
                ? DataAccess.Warning(reason)
                : null;

        public string Describe() =>
            Entry.Database == Database ? Entry.Describe() : $"{Entry.Describe()} on {Database}";

        // Every result names its target. It is the one guard against a wrong one that does not rely
        // on the model behaving: a mistake shows in the answer rather than three questions later.
        //
        // The warning on reading the data comes once, with the first result after the connection is
        // chosen: it informs a decision rather than a call, and repeating it would cost tokens on each.
        //
        // Line endings are normalised here, once, for everything the tools return: a carriage return
        // per line buys a reader nothing and is paid for in tokens on every result.
        public string Stamp(string body)
        {
            var warning = Interlocked.Exchange(ref _announced, 1) == 0 ? ReadAccessWarning : null;
            var heading = warning is null ? $"[{Describe()}]" : $"[{Describe()}]\n{warning}";
            return $"{heading}\n{body}".Replace("\r\n", "\n");
        }
    }
}
