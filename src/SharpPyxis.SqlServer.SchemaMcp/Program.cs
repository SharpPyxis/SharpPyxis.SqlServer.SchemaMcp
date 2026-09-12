using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpPyxis.SqlServer.SchemaMcp;

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol: every log goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var settings = SchemaSettings.FromEnvironment();

builder.Services
    .AddMcpServer(options => options.ServerInstructions =
        $"Read-only access to the DDL of one SQL Server database: {settings.Target}. "
        + "It returns object definitions only, never data. What it can read is set by the rights "
        + "granted to its login, not by this server.")
    .WithStdioServerTransport()
    .WithTools(new SchemaTools(settings).CreateTools());

await builder.Build().RunAsync();
