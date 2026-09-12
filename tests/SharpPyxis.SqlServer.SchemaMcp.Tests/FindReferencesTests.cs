using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class FindReferencesTests(DemoDatabase database)
{
    [Fact]
    public async Task ReferencingListsTheModulesUsingAnObject()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindReferences("sales.order_line"));

        Assert.Equal(
            ["sales.order_cancel", "sales.order_summary", "sales.order_total", "stock.item_restock"],
            rows.Select(row => row.Split('\t')[1]));
    }

    [Fact]
    public async Task ReferencingIncludesTheTablesWhoseForeignKeysPointToIt()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindReferences("sales.order_header"));

        Assert.Contains(rows, row => row.Contains("\tsales.order_line\t") && row.EndsWith("\tforeign key fk_order_line_order_header"));
    }

    [Fact]
    public async Task DynamicSqlIsNotInTheGraphAndTheResultSaysSo()
    {
        var result = await database.CreateTools().FindReferences("sales.order_line");

        Assert.DoesNotContain("order_export", result);
        Assert.Contains("search_modules", result);
    }

    [Fact]
    public async Task ObjectTypeTableKeepsTheForeignKeysOnly()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindReferences("sales.customer", objectType: "table"));

        Assert.Contains("\tsales.order_header\t", Assert.Single(rows));
    }

    [Fact]
    public async Task TooManyReferencesReturnTheSpreadByType()
    {
        var result = await database.CreateTools(maxResults: 2).FindReferences("sales.order_line");

        Assert.Contains("4 objects reference sales.order_line, more than the 2", result);
        Assert.Contains("By type", result);
    }

    [Fact]
    public async Task ReferencedListsWhatAModuleUsesOncePerObject()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().FindReferences("sales.order_report", direction: "referenced"));

        Assert.Equal(
            ["sales.customer", "sales.order_summary", "sales.order_total"],
            rows.Select(row => row.Split('\t')[1]));
    }

    [Fact]
    public async Task ReferencedMarksAMissingObjectAndTheBrokenModule()
    {
        var result = await database.CreateTools().FindReferences("audit.audit_write", direction: "referenced");

        Assert.Contains("\taudit.journal\tunresolved", result);
        Assert.Contains("It may no longer compile.", result);
    }

    [Fact]
    public async Task ReferencedNamesTheOtherDatabase()
    {
        var result = await database.CreateTools().FindReferences("audit.audit_copy", direction: "referenced");

        Assert.Contains("\tmsdb.dbo.sysjobs", result);
    }

    [Fact]
    public async Task ReferencedByATableGivesTheTablesItsForeignKeysPointTo()
    {
        var result = await database.CreateTools().FindReferences("sales.order_line", direction: "referenced");

        Assert.Contains("USER_TABLE\tsales.order_header\tforeign key fk_order_line_order_header", result);
    }

    [Fact]
    public async Task AnObjectNobodyUsesIsReportedWithTheReminder()
    {
        var result = await database.CreateTools().FindReferences("[legacy].[odd[name]");

        Assert.Contains("Nothing references legacy.odd[name.", result);
        Assert.Contains("search_modules", result);
    }

    [Fact]
    public async Task AnUnknownObjectIsReportedRatherThanFoundUnused()
    {
        var result = await database.CreateTools().FindReferences("sales.nothing");

        Assert.Contains("Object sales.nothing not found.", result);
    }
}
