using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpPyxis.SqlServer.SchemaMcp;

var settings = SchemaSettings.FromEnvironment();
var store = new ConnectionStore(settings.ConfigPath);

var verb = args is [var first, ..] ? first.ToLowerInvariant() : null;

// Credentials are typed here, in the foreground, started by the user. The server never asks.
if (verb == "configure")
    return Configurator.Run(store, args);

// An MCP client always launches this with stdin on a pipe; a person launching it from a console
// does not. That is what tells a server start from someone looking for the command line — and
// 'serve' says it outright, for a configuration that would rather be explicit.
if (verb != "serve" && !Console.IsInputRedirected)
{
    Console.WriteLine("""
        SharpPyxis.SqlServer.SchemaMcp — read-only access to the DDL of SQL Server databases.

        This is an MCP server: an MCP client starts it and speaks to it over standard input.
        Started from a console, it has nothing to say, so it shows this instead.

          schemamcp configure   manage the connections it works against
          schemamcp serve       run the server against this console, for debugging
        """);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol: every log goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options => options.ServerInstructions =
        "Read-only access to the DDL of SQL Server databases. It returns object definitions only, "
        + "never data. What it can read is set by the rights granted to its login, not by this server. "
        + "Call use_connection to see the configured targets and to choose one; every result names the "
        + "target it came from.")
    .WithStdioServerTransport()
    .WithTools(new SchemaTools(settings, store).CreateTools());

await builder.Build().RunAsync();
return 0;
