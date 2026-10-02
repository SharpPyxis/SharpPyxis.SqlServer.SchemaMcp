using System.Text.Json;
using Xunit;

namespace SharpPyxis.SqlServer.SchemaMcp.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class ArgumentCheckTests(DemoDatabase database)
{
    [Fact]
    public void ACallWithoutArgumentsIsToldWhatTheToolRequires()
    {
        var result = ArgumentCheck.Check("script_object", Schema("script_object"), Arguments("{}"));

        Assert.Equal("script_object requires objectName, which the call did not provide. Nothing was run. "
                     + "Arguments received: none. Parameters of script_object: objectName (string, required). "
                     + "Call it again with these names.",
                     result);
    }

    [Fact]
    public void ARenamedArgumentIsNamedBack()
    {
        var result = ArgumentCheck.Check("script_object", Schema("script_object"), Arguments("""{"object_name": "sales.orders"}"""));

        Assert.StartsWith("script_object has no parameter object_name; script_object requires objectName", result);
        Assert.Contains("Arguments received: object_name = \"sales.orders\".", result);
    }

    // The SDK drops an argument it does not know: the call would run unfiltered and look filtered.
    [Fact]
    public void AMisspelledOptionalArgumentIsRefused()
    {
        var result = ArgumentCheck.Check("list_objects", Schema("list_objects"), Arguments("""{"object_type": "view"}"""));

        Assert.StartsWith("list_objects has no parameter object_type. Nothing was run.", result);
        Assert.Contains("objectType (string, optional)", result);
        Assert.Contains("modifiedSince (string, date-time, optional)", result);
    }

    [Fact]
    public void ARequiredArgumentSentAsNullIsMissing()
    {
        var result = ArgumentCheck.Check("script_object", Schema("script_object"), Arguments("""{"objectName": null}"""));

        Assert.StartsWith("script_object requires objectName, which the call did not provide.", result);
    }

    [Fact]
    public void ACompleteCallGoesThrough()
    {
        Assert.Null(ArgumentCheck.Check("find_references", Schema("find_references"),
            Arguments("""{"objectName": "sales.orders", "limit": "3", "countOnly": true}""")));
    }

    [Fact]
    public void AToolWithoutParametersAcceptsAnEmptyCall()
    {
        Assert.Null(ArgumentCheck.Check("server_info", Schema("server_info"), Arguments("{}")));
    }

    private JsonElement Schema(string toolName) =>
        database.CreateTools().CreateTools().Single(tool => tool.ProtocolTool.Name == toolName).ProtocolTool.InputSchema;

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
