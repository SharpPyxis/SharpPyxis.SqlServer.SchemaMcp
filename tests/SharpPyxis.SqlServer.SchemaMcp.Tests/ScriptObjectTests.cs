using System.Text.RegularExpressions;
using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class ScriptObjectTests(DemoDatabase database)
{
    [Fact]
    public void AnUnqualifiedNameHeldByOneSchemaIsResolved()
    {
        var result = database.CreateTools().ScriptObject("order_place");

        Assert.Contains("insert into sales.order_header", result);
    }

    [Fact]
    public void AnUnqualifiedNameHeldBySeveralSchemasAsksToQualify()
    {
        var result = database.CreateTools().ScriptObject("customer");

        Assert.Contains("Qualify it: legacy.customer, sales.customer.", result);
    }

    [Fact]
    public void BracketedNamesAreAccepted()
    {
        var result = database.CreateTools().ScriptObject("[legacy].[odd[name]");

        Assert.Contains("CREATE TABLE", result);
    }

    [Fact]
    public void ATableKeepsOnlyTheCollationsThatAreNotTheDatabases()
    {
        var result = database.CreateTools().ScriptObject("legacy.customer");

        Assert.Contains("[legacy_code] [varchar](20) COLLATE Latin1_General_BIN2 NOT NULL", result);
        Assert.Contains("[name] [nvarchar](200) NULL", result);
        Assert.Single(Regex.Matches(result, "COLLATE"));
    }

    [Fact]
    public void RunsOfBlankLinesAreCollapsed()
    {
        var result = database.CreateTools().ScriptObject("sales.order_report");

        Assert.Contains("select count(*) as customers from sales.customer", result);
        Assert.DoesNotContain("\n\n\n", result);
    }

    [Fact]
    public void ATriggerIsScriptedFromUnderItsTable()
    {
        var result = database.CreateTools().ScriptObject("logistics.tr_stock_movement_touch");

        Assert.Contains("tr_stock_movement_touch", result);
        Assert.Contains("after insert", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownObjectIsReported()
    {
        var result = database.CreateTools().ScriptObject("sales.nothing");

        Assert.Contains("Object sales.nothing not found.", result);
    }
}
