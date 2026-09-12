using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

// In the demo database collection so that it never runs alongside another test: one of them sets an
// environment variable of the process.
[Collection(DemoDatabaseCollection.Name)]
public sealed class ReadConventionsTests(DemoDatabase database) : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"schema-mcp-conventions-{Guid.NewGuid():N}.md");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void WithoutAFileTheToolIsNotOffered()
    {
        var names = database.CreateTools().CreateTools().Select(tool => tool.ProtocolTool.Name);

        Assert.DoesNotContain("read_conventions", names);
    }

    [Fact]
    public void WithAFileTheToolIsOfferedAndReturnsItAsWritten()
    {
        File.WriteAllText(_file, "# Our conventions\r\n\r\nSQL is written in lower case.\r\n");
        var tools = database.CreateTools(conventionsPath: _file);

        Assert.Contains("read_conventions", tools.CreateTools().Select(tool => tool.ProtocolTool.Name));
        Assert.Equal("# Our conventions\n\nSQL is written in lower case.\n", tools.ReadConventions());
    }

    [Fact]
    public void ALongFileIsCutAndSaysSo()
    {
        File.WriteAllText(_file, new string('x', 60_000));

        var result = database.CreateTools(conventionsPath: _file).ReadConventions();

        Assert.StartsWith(new string('x', 50_000), result);
        Assert.EndsWith("(The conventions file holds 60000 characters: only the first 50000 are returned.)", result);
    }

    [Fact]
    public void AFileRemovedAfterStartIsReported()
    {
        File.WriteAllText(_file, "conventions");
        var tools = database.CreateTools(conventionsPath: _file);
        File.Delete(_file);

        Assert.Contains("no longer exists", tools.ReadConventions());
    }

    [Fact]
    public void ServerInfoSaysWhereTheFileIsLookedFor()
    {
        Assert.Contains("Conventions file: none; a file at ", database.CreateTools().ServerInfo());

        File.WriteAllText(_file, "conventions");
        Assert.Contains($"Conventions file: {_file}, offered by read_conventions", database.CreateTools(conventionsPath: _file).ServerInfo());
    }

    [Fact]
    public void TheVariableWinsOverTheFileNextToTheExecutable()
    {
        var previous = Environment.GetEnvironmentVariable("DDL_CONVENTIONS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DDL_CONVENTIONS_PATH", _file);
            Assert.Equal(_file, SchemaSettings.FromEnvironment().ConventionsPath);

            Environment.SetEnvironmentVariable("DDL_CONVENTIONS_PATH", null);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "conventions.md"), SchemaSettings.FromEnvironment().ConventionsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DDL_CONVENTIONS_PATH", previous);
        }
    }
}
