namespace Jacobi.BinarySerializer.Schema;

public enum SchemaNodeKind
{
    Schema,
    Field,
    Group,
    Repeat,
    Choice,
}

public enum SchemaDataType
{
    String,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Boolean,
    Double,
    DateTime,
}

public closed class SchemaNode
{
    public required string Name { get; init; }
    public SchemaNodeKind Kind { get; protected init; }
    public required IReadOnlyList<SchemaProperty> Properties { get; init; }
}

public sealed class SchemaField : SchemaNode
{
    public SchemaField()
    {
        Kind = SchemaNodeKind.Field;
    }

    public required SchemaCodecRef Codec { get; init; }
    public required SchemaDataType Type { get; init; }
}

public class SchemaGroup : SchemaNode
{
    public SchemaGroup()
    {
        Kind = SchemaNodeKind.Group;
    }

    public required IReadOnlyList<SchemaCodecRef> Pipeline { get; init; }
    public required IReadOnlyList<SchemaNode> Children { get; init; }
}

public sealed class SchemaRepeat : SchemaGroup
{
    public SchemaRepeat()
    {
        Kind = SchemaNodeKind.Repeat;
    }

    public required SchemaCodecOrValue<int> Count { get; init; }
}

public sealed class SchemaChoice : SchemaGroup
{
    public SchemaChoice()
    {
        Kind = SchemaNodeKind.Choice;
    }

    public required SchemaCodecOrValue<int> SelectedIndex { get; init; }
}

public class Schema : SchemaNode
{
    public Schema()
    {
        Kind = SchemaNodeKind.Schema;
    }

    public required IReadOnlyList<SchemaGroup> Children { get; init; }
    public required IReadOnlyList<SchemaNode> TypeDefs { get; init; }
    public required IReadOnlyList<SchemaCodecRef> CodecDefs { get; init; }

    public required IReadOnlyList<SchemaDocumentRef> Includes { get; init; }
}

public sealed class SchemaDocument : Schema
{
    public required IReadOnlyList<SchemaGroup> Roots { get; init; }

    public required IReadOnlyList<SchemaGroup> Groups { get; init; }
    public required IReadOnlyList<SchemaField> Fields { get; init; }
}

public sealed class SchemaDocumentRef
{
    public required string Schema { get; init; }
    public string? Path { get; init; }

    // filled after loading the schema document references
    public SchemaDocument? SchemaDocument { get; internal set; }
}

public sealed class SchemaCodecRef
{
    // Either the codec-id name or the CodecDefs name.
    public required string Codec { get; init; }
    public required IReadOnlyList<SchemaProperty> Properties { get; init; }
}

public union SchemaCodecOrValue<T>(SchemaCodecRef, T) { }

public sealed class SchemaProperty
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}
