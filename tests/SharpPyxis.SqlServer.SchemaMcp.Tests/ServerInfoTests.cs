using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class ServerInfoTests(DemoDatabase database)
{
    [Fact]
    public void GivesTheCommandThatRemovesAConnection()
    {
        var result = database.CreateTools().ServerInfo();

        Assert.Contains("Executable: ", result);
        Assert.Contains("configure remove <id>", result);
    }

    [Fact]
    public void SaysWhenTheConnectionComesFromTheEnvironment()
    {
        var result = database.CreateTools().ServerInfo();

        Assert.Contains("declared by the CONNECTION_STRING environment variable", result);
    }
}
