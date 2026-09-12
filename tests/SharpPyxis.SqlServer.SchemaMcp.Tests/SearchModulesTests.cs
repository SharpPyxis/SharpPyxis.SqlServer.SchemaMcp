using System.Text.RegularExpressions;
using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed partial class SearchModulesTests(DemoDatabase database)
{
    // What find_references cannot see is what this tool exists for.
    [Fact]
    public async Task FindsATableNamedInsideAStringOfDynamicSql()
    {
        var result = await database.CreateTools().SearchModules("sales.order_line");

        Assert.Contains("\tsales.order_export\t", result);
        Assert.Contains("declare @sql nvarchar(max) = N'select * from sales.order_line", result);
    }

    [Fact]
    public async Task OccurrencesAreNumberedAndDistantWindowsKeptApart()
    {
        var result = await database.CreateTools().SearchModules("select", name: "order_report", context: 1);

        var numbered = NumberedLines(result);
        var occurrences = numbered.Where(line => line.Occurrence).Select(line => line.Number).ToList();

        Assert.Equal(2, occurrences.Count);
        Assert.Equal(6, occurrences[1] - occurrences[0]);
        Assert.Equal(6, numbered.Count);
        Assert.Contains("...", result.Split('\n'));
    }

    [Fact]
    public async Task WindowsThatOverlapAreMerged()
    {
        var result = await database.CreateTools().SearchModules("select", name: "order_report", context: 3);

        var numbers = NumberedLines(result).Select(line => line.Number).ToList();

        Assert.DoesNotContain("...", result.Split('\n'));
        Assert.Equal(numbers.Distinct(), numbers);
    }

    [Fact]
    public async Task TooManyModulesReturnASampleRatherThanReadEverything()
    {
        var result = await database.CreateTools(maxResults: 2).SearchModules("sales.");

        Assert.Contains("More than 2 modules contain 'sales.'", result);
        Assert.Contains("By schema", result);
    }

    [Fact]
    public async Task ASampleFromOneSchemaFallsBackOnNamePrefixes()
    {
        var result = await database.CreateTools(maxResults: 2).SearchModules("sales.", schema: "sales");

        Assert.Contains("By name prefix", result);
    }

    [Fact]
    public async Task LinesPastTheCapAreCutAndSaidSo()
    {
        var result = await database.CreateTools(maxResults: 2).SearchModules("sales.", name: "order_cancel");

        Assert.Equal(2, NumberedLines(result).Count);
        Assert.Contains("(Lines capped at 2.", result);
    }

    [Fact]
    public async Task PatternCharactersAreTakenLiterally()
    {
        var result = await database.CreateTools().SearchModules("order_line", schema: "stock");

        Assert.Contains("\tstock.item_restock\t", result);
        Assert.DoesNotContain("No module", result);

        var none = await database.CreateTools().SearchModules("order%line");

        Assert.Contains("No module contains 'order%line'.", none);
    }

    private static List<(int Number, bool Occurrence)> NumberedLines(string result) =>
        [.. result.Split('\n')
            .Select(line => NumberedLine().Match(line))
            .Where(match => match.Success)
            .Select(match => (int.Parse(match.Groups[1].Value), match.Groups[2].Value == ":"))];

    [GeneratedRegex(@"^(\d+)([:-]) ")]
    private static partial Regex NumberedLine();
}
