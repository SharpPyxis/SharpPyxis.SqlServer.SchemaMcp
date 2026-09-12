using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class FindColumnsTests(DemoDatabase database)
{
    [Fact]
    public async Task FindsEveryTableCarryingAColumn()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindColumns("siret"));

        Assert.Equal(
            ["USER_TABLE\tlegacy.customer\tsiret\tchar(14)\tnull", "USER_TABLE\tsales.customer\tsiret\tchar(14)\tnull"],
            rows);
    }

    [Fact]
    public async Task EqualsKeepsTheExactNameOnly()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindColumns("name", columnMatch: "equals"));

        Assert.All(rows, row => Assert.Equal("name", row.Split('\t')[2]));
        Assert.Contains(rows, row => row.StartsWith("USER_TABLE\tsales.customer\tname\tnvarchar(200)\tnot null"));
    }

    [Fact]
    public async Task ViewsCarryTheirColumns()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindColumns("line_count"));

        Assert.Equal("VIEW\tsales.order_summary\tline_count\tint\tnull", Assert.Single(rows));
    }

    [Fact]
    public async Task TypesAreWrittenAsTheDdlWritesThem()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindColumns("unit_price"));

        Assert.EndsWith("\tdecimal(18, 4)\tnot null", Assert.Single(rows));
    }

    [Fact]
    public async Task TooManyMatchesReturnTheSpreadByColumnName()
    {
        var result = await database.CreateTools(maxResults: 3).FindColumns("id");

        Assert.Contains("columns match, more than the 3 this server returns at once.", result);
        Assert.Contains("By column name", result);
    }

    [Fact]
    public async Task CountOnlyUnderTheCapDoesNotClaimAnOverflow()
    {
        var result = await database.CreateTools().FindColumns("siret", countOnly: true);

        Assert.Contains("2 columns match.", result);
        Assert.DoesNotContain("more than", result);
    }

    [Fact]
    public async Task AnExactNameSpreadNeitherBreaksDownByNameNorSuggestsEquals()
    {
        var result = await database.CreateTools().FindColumns("name", columnMatch: "equals", countOnly: true);

        Assert.DoesNotContain("By column name", result);
        Assert.DoesNotContain("columnMatch 'equals'", result);
    }

    [Fact]
    public async Task AColumnNobodyCarriesIsReported()
    {
        var result = await database.CreateTools().FindColumns("no_such_column");

        Assert.Contains("No table or view has a column matching 'no_such_column'.", result);
    }
}
