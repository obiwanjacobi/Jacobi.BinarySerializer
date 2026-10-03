namespace Jacobi.BinarySerializer.Schema;

public closed class SchemaNode
{
    public required string Name { get; init; }
    public SchemaNodeKind Kind { get; protected init; }
    public IReadOnlyList<SchemaProperty> Properties => PropertyList;
    internal List<SchemaProperty> PropertyList { get; init; } = [];
    /// <summary>
    /// Optional reference to a SchemaTypeDef that defines the field's type and processors.
    /// </summary>
    public SchemaName? TypeDef { get; init; }
}

public sealed class SchemaTypeDef : SchemaNode
{
    public required IReadOnlyList<SchemaProcessorRef> Processors { get; init; }
    public SchemaDataType Type { get; init; } = SchemaDataType.None;
}

public sealed class SchemaField : SchemaNode
{
    public SchemaField()
    {
        Kind = SchemaNodeKind.Field;
    }

    public IReadOnlyList<SchemaProcessorRef> Processors => ProcessorsList;
    public List<SchemaProcessorRef> ProcessorsList { get; init; } = [];
    public required SchemaDataType Type { get; init; }
}

public class SchemaGroup : SchemaNode
{
    public SchemaGroup()
    {
        Kind = SchemaNodeKind.Group;
    }

    public IReadOnlyList<SchemaProcessorRef> Processors => ProcessorsList;
    public List<SchemaProcessorRef> ProcessorsList { get; init; } = [];
    public IReadOnlyList<SchemaNode> Children => ChildList;
    internal List<SchemaNode> ChildList { get; init; } = [];
}

public sealed class SchemaRepeat : SchemaGroup
{
    public SchemaRepeat()
    {
        Kind = SchemaNodeKind.Repeat;
    }

    public required SchemaValueOrRef<int> Count { get; init; }
}

public sealed class SchemaChoice : SchemaGroup
{
    public SchemaChoice()
    {
        Kind = SchemaNodeKind.Choice;
    }

    public required SchemaValueOrRef<int> SelectedIndex { get; init; }
}

public readonly union SchemaObject(SchemaField, SchemaGroup, SchemaRepeat, SchemaChoice);

public class Schema : SchemaGroup
{
    public Schema()
    {
        Kind = SchemaNodeKind.Schema;
    }

    public required IReadOnlyList<SchemaTypeDef> TypeDefs { get; init; }
    public required IReadOnlyList<SchemaProcessorRef> ProcessorDefs { get; init; }

    public required IReadOnlyList<SchemaDocumentRef> Includes { get; init; }
}

public sealed class SchemaDocument : Schema
{
    public required IReadOnlyList<SchemaGroup> Roots { get; init; }

    public required IReadOnlyList<SchemaGroup> Groups { get; init; }
    public required IReadOnlyList<SchemaField> Fields { get; init; }

    /// <summary>
    /// True when all references have been resolved and the schema document is ready for use.
    /// </summary>
    public bool IsCompiled { get; internal set; }
}

public sealed class SchemaDocumentRef
{
    public required string Schema { get; init; }
    public string? Path { get; init; }

    // filled after loading the schema document references
    public SchemaDocument? SchemaDocument { get; internal set; }
}

public sealed class SchemaProcessorRef
{
    // Either the processor-id name or the ProcessorDefs name.
    public required SchemaName Processor { get; init; }
    public IReadOnlyList<SchemaProperty> Properties => PropertyList;
    internal List<SchemaProperty> PropertyList { get; init; } = [];
}

public union SchemaValueOrRef<T>(SchemaValueRef, T) { }

/// <summary>
/// A reference to a (public) value: either a value published by a processor ('pubns/name')
/// or the value of a field addressed by its schema path ('Root.Header.Length').
/// </summary>
public sealed class SchemaValueRef
{
    public const char PublishedSeparator = '/';

    public required string Reference { get; init; }

    /// <summary>True for 'pubns/name', false for a schema path.</summary>
    public bool IsPublished => Reference.Contains(PublishedSeparator);
}

public sealed class SchemaProperty
{
    public required string Name { get; init; }
    /// <summary>
    /// The value-string is interpeted/parsed by the processor that uses this property.
    /// It may be a literal value or a reference to a processor that provides the value.
    /// </summary>
    public required string Value { get; init; }
    // TODO: allow complex objects as value, e.g. a list of values/object structures etc.
}

public enum SchemaNodeKind
{
    /// <summary>Root container object for a schema.</summary>
    Schema,
    /// <summary>A reusable type definition.</summary>
    TypeDef,
    /// <summary>A field within a group.</summary>
    Field,
    /// <summary>A group of fields.</summary>
    Group,
    /// <summary>A repeated field or group.</summary>
    Repeat,
    /// <summary>A choice between multiple fields or groups.</summary>
    Choice,
}

public enum SchemaDataType
{
    /// <summary>Not set/not used (for group typedefs)</summary>
    None,
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
