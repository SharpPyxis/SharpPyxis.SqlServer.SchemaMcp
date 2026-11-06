using System;
using System.Collections;
using System.Data.SqlTypes;
using System.IO;
using Microsoft.SqlServer.Server;

namespace SharpPyxis.SqlServer.SchemaMcp.ClrSample;

/// <summary>One method per kind of CLR module: a procedure, two functions and a trigger.</summary>
public static class Samples
{
    [SqlProcedure]
    public static void Greet(SqlString name, out SqlString greeting) => greeting = "hello " + name.Value;

    [SqlFunction]
    public static SqlInt32 Twice(SqlInt32 value) => value * 2;

    [SqlFunction(FillRowMethodName = nameof(FillItem), TableDefinition = "item nvarchar(100)")]
    public static IEnumerable Split(SqlString text) => text.Value.Split(',');

    public static void FillItem(object row, out SqlString item) => item = (string)row;

    [SqlTrigger]
    public static void Touch()
    {
    }
}

/// <summary>An aggregate, which SQL Server keeps apart from the other modules.</summary>
[Serializable]
[SqlUserDefinedAggregate(Format.UserDefined, MaxByteSize = 8000)]
public struct Concat : IBinarySerialize
{
    private string _text;

    public void Init() => _text = string.Empty;

    public void Accumulate(SqlString value) => _text += value.Value;

    public void Merge(Concat other) => _text += other._text;

    public SqlString Terminate() => _text;

    public void Read(BinaryReader reader) => _text = reader.ReadString();

    public void Write(BinaryWriter writer) => writer.Write(_text);
}
