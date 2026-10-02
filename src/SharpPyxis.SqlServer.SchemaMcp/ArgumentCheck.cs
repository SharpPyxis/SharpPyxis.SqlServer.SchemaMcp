using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.SchemaMcp;

/// <summary>
/// Answers a call the tool cannot take with what the tool expects. The SDK sends the model a bare
/// "An error occurred" for any failure, with nothing to correct, so the model repeats the same call;
/// and it drops an argument whose name it does not know, so a misspelled filter returns the
/// unfiltered result as if it had applied.
/// </summary>
internal static class ArgumentCheck
{
    /// <summary>The call tool filter, checking each call against the input schema of its tool.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter(IEnumerable<McpServerTool> tools)
    {
        var schemas = tools.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema);

        return next => async (context, cancellationToken) =>
        {
            var call = context.Params;
            if (call is null || !schemas.TryGetValue(call.Name, out var schema))
                return await next(context, cancellationToken);

            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ArgumentCheck));
            var arguments = call.Arguments?.ToDictionary() ?? [];

            // The log takes the names only, never the values: they are what tells a client that renames
            // its arguments from one that sends none.
            var names = arguments.Count == 0 ? "none" : string.Join(", ", arguments.Keys);

            if (Check(call.Name, schema, arguments) is { } refusal)
            {
                logger?.LogWarning("{Tool} refused. Arguments received: {Received}.", call.Name, names);
                return Refuse(refusal);
            }

            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The SDK is the one that converts the values, and it accepts more than the schema says
                // ("3" for an integer): its own message is the only faithful account of a refusal. It
                // does not name the argument, so the values received are given back beside it.
                logger?.LogError(exception, "{Tool} failed. Arguments received: {Received}.", call.Name, names);
                return Refuse($"{call.Name} failed: {exception.Message} Arguments received: {Describe(arguments)}. "
                              + $"Parameters of {call.Name}: {DescribeParameters(schema)}.");
            }
        };
    }

    /// <summary>
    /// The message for a call to <paramref name="toolName"/> that names an argument the tool does not
    /// have or lacks a required one, or null when the names match. A required argument sent as null
    /// counts as missing: the tool would receive nothing either way.
    /// </summary>
    public static string? Check(string toolName, JsonElement schema, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var known = schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];

        var unknown = arguments.Keys.Where(name => !known.Contains(name)).ToList();
        var missing = Required(schema)
            .Where(name => !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            .ToList();
        if (unknown.Count == 0 && missing.Count == 0)
            return null;

        var faults = new List<string>();
        if (unknown.Count > 0)
            faults.Add($"{toolName} has no parameter {string.Join(", ", unknown)}");
        if (missing.Count > 0)
            faults.Add($"{toolName} requires {string.Join(", ", missing)}, which the call did not provide");

        return $"{string.Join("; ", faults)}. Nothing was run. Arguments received: {Describe(arguments)}. "
               + $"Parameters of {toolName}: {DescribeParameters(schema)}. Call it again with these names.";
    }

    private static List<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(name => name.GetString()!).ToList()
            : [];

    private static string DescribeParameters(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.EnumerateObject().Any())
            return "none";

        var required = Required(schema);
        return string.Join("; ", properties.EnumerateObject().Select(property =>
            $"{property.Name} ({DescribeType(property.Value)}, {(required.Contains(property.Name) ? "required" : "optional")})"));
    }

    // The schema writes a nullable type as ["integer", "null"]: what the model needs is the type, and
    // "optional" already says it can be left out.
    private static string DescribeType(JsonElement parameter)
    {
        if (!parameter.TryGetProperty("type", out var type))
            return "any";

        var name = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(item => item.GetString()).First(item => item != "null")
            : type.GetString();

        return parameter.TryGetProperty("format", out var format) ? $"{name}, {format.GetString()}" : name!;
    }

    // A value is written as JSON, so the model sees "true" (a string) apart from true (a boolean).
    private static string Describe(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments.Count == 0
            ? "none"
            : string.Join(", ", arguments.Select(argument => $"{argument.Key} = {Shorten(argument.Value.GetRawText())}"));

    private static string Shorten(string value) => value.Length <= 80 ? value : $"{value[..80]}…";

    private static CallToolResult Refuse(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
