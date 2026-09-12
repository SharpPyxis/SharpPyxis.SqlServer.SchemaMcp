using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpPyxis.SqlServer.SchemaMcp;

var settings = SchemaSettings.FromEnvironment();
var store = new ConnectionStore(settings.ConfigPath);

// Credentials are typed here, in the foreground, started by the user. The server itself never asks.
if (args is [var verb, ..] && string.Equals(verb, "configure", StringComparison.OrdinalIgnoreCase))
    return Configurator.Run(store, args);

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
