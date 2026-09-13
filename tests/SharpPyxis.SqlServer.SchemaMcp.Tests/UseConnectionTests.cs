using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class UseConnectionTests(DemoDatabase database)
{
    // The connection of the demo names its database: another database of the same server is refused.
    [Fact]
    public void AConnectionThatNamesADatabaseStaysOnIt()
    {
        var result = database.CreateTools().UseConnection(1, "master");

        Assert.Contains("works on the database SchemaMcpTests, and only on it", result);
        Assert.DoesNotContain("Selected.", result);
    }

    [Fact]
    public void AConnectionThatNamesADatabaseAcceptsItsOwnName()
    {
        Assert.Contains("Selected.", database.CreateTools().UseConnection(1, "schemamcptests"));
    }

    [Fact]
    public void AServerLevelConnectionRefusesADatabaseItCannotOpen()
    {
        var tools = database.CreateTools(entry: database.Entry with { Database = null });

        var result = tools.UseConnection(1, "no_such_database");

        Assert.Contains("No database named no_such_database can be opened by this login.", result);
        Assert.Contains("SchemaMcpTests", result);
        Assert.DoesNotContain("Selected.", result);
    }

    // The name is taken as the server spells it, whatever case the agent used.
    [Fact]
    public void AServerLevelConnectionSelectsADatabaseItCanOpen()
    {
        var tools = database.CreateTools(entry: database.Entry with { Database = null });

        var result = tools.UseConnection(1, "schemamcptests");

        Assert.Contains("Selected.", result);
        Assert.Contains("on SchemaMcpTests]", result);
    }
}
