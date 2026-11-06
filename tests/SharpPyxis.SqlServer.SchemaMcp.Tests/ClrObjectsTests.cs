using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

/// <summary>The CLR objects of tests/Create-ClrObjects.sql, one of each kind, read by every tool that sees them.</summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class ClrObjectsTests(DemoDatabase database)
{
    private const string Samples = "[clr_sample].[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Samples]";

    [Fact]
    public async Task EveryKindIsListedWithItsTypeWordAndNoLength()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().ListObjects(schema: "clr"))
            .Select(row => row.Split('\t'))
            .Where(row => row[0] != "table")
            .Select(row => (row[0], row[1], row[^1]));

        Assert.Equal(
            [("aggregate", "clr.concat", ""), ("clr procedure", "clr.greet", ""), ("clr table function", "clr.split", ""),
             ("clr trigger", "clr.tr_journal_touch", ""), ("clr scalar function", "clr.twice", "")],
            rows);
    }

    [Theory]
    [InlineData("procedure", "clr.greet")]
    [InlineData("trigger", "clr.tr_journal_touch")]
    [InlineData("aggregate", "clr.concat")]
    public async Task TheFilterWordsCoverTheClrKinds(string objectType, string expected)
    {
        var result = await database.CreateTools().ListObjects(schema: "clr", objectType: objectType);

        Assert.Contains($"\t{expected}\t", Assert.Single(ToolOutput.Rows(result)));
    }

    [Fact]
    public async Task FunctionCoversBothClrFunctions()
    {
        var rows = ToolOutput.Rows(await database.CreateTools().ListObjects(schema: "clr", objectType: "function"));

        Assert.Equal(2, rows.Length);
    }

    [Fact]
    public void AProcedureGivesItsParametersAndItsExternalName()
    {
        var result = database.CreateTools().DescribeObject("clr.greet");

        Assert.Contains("clr procedure\tclr.greet\n", result);
        Assert.Contains($"External name: {Samples}.[Greet], in an assembly of permission set SAFE", result);
        Assert.Contains("Parameters:\n@name\tnvarchar(100)\n@greeting\tnvarchar(200)\toutput\n", result);
    }

    [Fact]
    public void ATableFunctionGivesTheColumnsItReturns()
    {
        var result = database.CreateTools().DescribeObject("clr.split");

        Assert.Contains("\nitem\tnvarchar(100)\tnull\n", result);
    }

    [Fact]
    public void AnAggregateGivesWhatItTakesAndReturnsAndAClassWithoutMethod()
    {
        var result = database.CreateTools().DescribeObject("clr.concat");

        Assert.Contains("External name: [clr_sample].[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Concat], in an", result);
        Assert.Contains("\n@value\tnvarchar(4000)\n", result);
        Assert.Contains("\nreturns\tnvarchar(4000)\n", result);
    }

    [Fact]
    public void ATriggerGivesTheTableItFiresOn()
    {
        var result = database.CreateTools().DescribeObject("clr.tr_journal_touch");

        Assert.Contains("On: clr.journal, after insert", result);
    }

    [Theory]
    [InlineData("clr.greet", "CREATE PROCEDURE [clr].[greet]", $"EXTERNAL NAME {Samples}.[Greet]")]
    [InlineData("clr.twice", "CREATE FUNCTION [clr].[twice]", $"EXTERNAL NAME {Samples}.[Twice]")]
    [InlineData("clr.concat", "CREATE AGGREGATE [clr].[concat]", "EXTERNAL NAME [clr_sample].[SharpPyxis.SqlServer.SchemaMcp.ClrSample.Concat]")]
    [InlineData("clr.tr_journal_touch", "CREATE TRIGGER [clr].[tr_journal_touch] ON [clr].[journal]", $"EXTERNAL NAME {Samples}.[Touch]")]
    public void EveryKindIsScriptedAsItsSignature(string objectName, string creates, string calls)
    {
        var result = database.CreateTools().ScriptObject(objectName);

        Assert.Contains(creates, result);
        Assert.Contains(calls, result);
    }

    [Fact]
    public void TheTableAClrFunctionReturnsKeepsOnlyCollationsThatAreNotTheDatabases()
    {
        var result = database.CreateTools().ScriptObject("clr.split");

        Assert.Contains("[item] [nvarchar](100) NULL", result);
        Assert.DoesNotContain("COLLATE", result);
    }

    [Fact]
    public async Task SearchModulesHasNoTextToReadInThem()
    {
        var result = await database.CreateTools().SearchModules("Greet", schema: "clr");

        Assert.DoesNotContain("clr.greet", result);
    }
}
