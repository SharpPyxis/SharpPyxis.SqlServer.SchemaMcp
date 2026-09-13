using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class DescribeObjectTests(DemoDatabase database)
{
    [Fact]
    public void ATableGivesItsRowCountColumnsKeysAndForeignKeys()
    {
        var result = database.CreateTools().DescribeObject("sales.order_line");

        Assert.Contains("table\tsales.order_line\tabout 0 rows (from the catalog: the rows themselves were not read)", result);
        Assert.Contains("\norder_id\tint\tnot null\n", result);
        Assert.Contains("\nunit_price\tdecimal(18, 4)\tnot null\n", result);
        Assert.Contains("Primary key: pk_order_line (order_id, line_number) clustered", result);
        Assert.Contains("Foreign key: fk_order_line_order_header (order_id) -> sales.order_header (order_id)", result);
    }

    [Fact]
    public void EverythingATableCanCarryIsReported()
    {
        var result = database.CreateTools().DescribeObject("logistics.stock_movement");

        Assert.Contains("\nstock_movement_id\tbigint\tnot null\tidentity(1, 1)\n", result);
        Assert.Contains("\tdefault (sysdatetimeoffset())", result);
        Assert.Contains("\nquantity_signed\tdecimal(18, 3)\tnull\tcomputed as ", result);
        Assert.Contains("Unique constraint: uq_stock_movement_reference (reference) nonclustered", result);
        Assert.Contains("Index: ix_stock_movement_item_out (item_code) nonclustered include (quantity) where ([direction]='out')", result);
        Assert.Contains("Foreign key: fk_stock_movement_item_a (item_code) -> stock.item_a (item_code) on delete cascade", result);
        Assert.Contains("Check: ck_stock_movement_direction ", result);
        Assert.Contains("Trigger: tr_stock_movement_touch", result);
    }

    [Fact]
    public void DescriptionsComeOnlyWhenAskedFor()
    {
        var tools = database.CreateTools();

        Assert.DoesNotContain("Movements in and out of stock", tools.DescribeObject("logistics.stock_movement"));

        var described = tools.DescribeObject("logistics.stock_movement", descriptions: true);
        Assert.Contains("Description: Movements in and out of stock.", described);
        Assert.Contains("\t-- Always positive: direction gives the sign.", described);
    }

    [Fact]
    public void ATriggerGivesTheTableItFiresOnAndWhen()
    {
        var result = database.CreateTools().DescribeObject("logistics.tr_stock_movement_touch");

        Assert.Contains("trigger\tlogistics.tr_stock_movement_touch", result);
        Assert.Contains("On: logistics.stock_movement, after insert", result);
    }

    [Fact]
    public void AViewGivesItsColumnsAndNoRowCount()
    {
        var result = database.CreateTools().DescribeObject("sales.order_summary");

        Assert.Contains("view\tsales.order_summary\n", result);
        Assert.Contains("\nline_count\tint\tnull\n", result);
    }

    [Fact]
    public void AProcedureGivesItsParameters()
    {
        var result = database.CreateTools().DescribeObject("sales.order_place");

        Assert.Contains("Parameters:\n@customer_id\tint\n", result);
    }

    [Fact]
    public void AScalarFunctionGivesWhatItReturns()
    {
        var result = database.CreateTools().DescribeObject("sales.order_total");

        Assert.Contains("\nreturns\tdecimal(18, 4)\n", result);
        Assert.Contains("\n@order_id\tint\n", result);
    }

    [Fact]
    public void ASequenceGivesItsRange()
    {
        var result = database.CreateTools().DescribeObject("sales.order_number");

        Assert.Contains("Sequence: int, start 1, increment 1, minimum -2147483648, maximum 2147483647", result);
    }

    [Fact]
    public void AnUnknownObjectIsReported()
    {
        Assert.Contains("Object sales.nothing not found.", database.CreateTools().DescribeObject("sales.nothing"));
    }
}
