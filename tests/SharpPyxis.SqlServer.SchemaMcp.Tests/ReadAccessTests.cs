using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

// LocalDB makes the Windows account that owns it sysadmin, which is the case the warning exists for.
// The opposite case, a restricted login, is not reachable from these tests.
[Collection(DemoDatabaseCollection.Name)]
public sealed class ReadAccessTests(DemoDatabase database)
{
    private const string Warning = "This login can read the data of the database (";

    [Fact]
    public async Task TheFirstResultAfterSelectionCarriesTheWarningAndTheNextOnesDoNot()
    {
        var tools = database.CreateTools();

        var first = await tools.ListObjects(schema: "stock");
        var second = await tools.ListObjects(schema: "stock");

        Assert.Contains(Warning, first);
        Assert.DoesNotContain(Warning, second);
    }

    [Fact]
    public void ServerInfoGivesTheWarningEachTimeItIsAsked()
    {
        var tools = database.CreateTools();

        Assert.Contains(Warning, tools.ServerInfo());
        Assert.Contains(Warning, tools.ServerInfo());
    }

    [Fact]
    public async Task TheWarningIsNotARowOfTheListing()
    {
        var result = await database.CreateTools().ListObjects(schema: "stock");

        Assert.Equal(3, ToolOutput.Rows(result).Length);
    }
}
