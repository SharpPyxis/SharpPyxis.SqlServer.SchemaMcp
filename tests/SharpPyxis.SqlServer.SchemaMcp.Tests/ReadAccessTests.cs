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

    // The demo database documents one table and one of its columns.
    [Fact]
    public async Task TheFirstResultSaysTheServerReadsNoDataAndCountsTheDescriptions()
    {
        var tools = database.CreateTools();

        var first = await tools.ListObjects(schema: "stock");
        var second = await tools.ListObjects(schema: "stock");

        Assert.Contains("This server reads no data", first);
        Assert.Contains("Descriptions (MS_Description) on objects: 1, on columns: 1.", first);
        Assert.DoesNotContain("This server reads no data", second);
        Assert.DoesNotContain("MS_Description", second);
    }

    [Fact]
    public async Task TheWarningIsNotARowOfTheListing()
    {
        var result = await database.CreateTools().ListObjects(schema: "stock");

        Assert.Equal(3, ToolOutput.Rows(result).Length);
    }
}
