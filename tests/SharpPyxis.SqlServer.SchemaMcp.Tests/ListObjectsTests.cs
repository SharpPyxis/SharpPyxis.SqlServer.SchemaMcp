using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class ListObjectsTests(DemoDatabase database)
{
    [Fact]
    public async Task EveryResultNamesItsTarget()
    {
        var result = await database.CreateTools().ListObjects(schema: "stock");

        Assert.StartsWith("[demo database (", result);
    }

    // An escape applied in the wrong order returns no row rather than an error, which is why the
    // three characters LIKE reads as a pattern each have a case.
    [Fact]
    public async Task EqualsDoesNotReadAnUnderscoreAsAnyCharacter()
    {
        var result = await database.CreateTools().ListObjects(name: "item_a", nameMatch: "equals");

        Assert.Contains("\tstock.item_a\t", Assert.Single(ToolOutput.Rows(result)));
    }

    [Theory]
    [InlineData("100%", "legacy.100%_done")]
    [InlineData("odd[", "legacy.odd[name")]
    public async Task ContainsMatchesNamesHoldingPatternCharacters(string fragment, string expected)
    {
        var result = await database.CreateTools().ListObjects(name: fragment);

        Assert.Contains($"\t{expected}\t", Assert.Single(ToolOutput.Rows(result)));
    }

    [Fact]
    public async Task ObjectTypeFilters()
    {
        var result = await database.CreateTools().ListObjects(schema: "stock", objectType: "procedure");

        Assert.Contains("\tstock.item_restock\t", Assert.Single(ToolOutput.Rows(result)));
    }

    [Fact]
    public async Task LengthIsGivenForModulesAndLeftEmptyForTables()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().ListObjects(schema: "sales"));

        var procedure = rows.Single(row => row.Contains("\tsales.order_place\t")).Split('\t');
        var table = rows.Single(row => row.Contains("\tsales.customer\t")).Split('\t');

        Assert.True(long.Parse(procedure[^1]) > 0);
        Assert.Equal(string.Empty, table[^1]);
    }

    [Fact]
    public async Task LimitReturnsItsRowsAndAnnouncesTheTotal()
    {
        var result = await database.CreateTools().ListObjects(limit: 2);

        Assert.Equal(2, ToolOutput.Rows(result).Length);
        Assert.Contains("(2 of ", result);
    }

    [Fact]
    public async Task TooManyMatchesReturnTheSpreadInsteadOfTheRows()
    {
        var result = await database.CreateTools(maxResults: 5).ListObjects();

        Assert.Contains("objects match, more than the 5 this server returns at once.", result);
        Assert.Contains("By schema", result);
        Assert.Contains("By type", result);
    }

    [Fact]
    public async Task CountOnlyUnderTheCapDoesNotClaimAnOverflow()
    {
        var result = await database.CreateTools().ListObjects(schema: "stock", countOnly: true);

        Assert.Contains("3 objects match.", result);
        Assert.DoesNotContain("more than", result);
    }

    [Fact]
    public async Task ASingleSchemaFallsBackOnNamePrefixes()
    {
        var result = await database.CreateTools(maxResults: 3).ListObjects(schema: "sales");

        Assert.Contains("By name prefix", result);
        Assert.Contains("\norder\t9\t", result);
    }
}
