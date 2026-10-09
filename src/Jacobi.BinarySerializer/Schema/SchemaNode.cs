using Jacobi.BinarySerializer.Processor;

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
    public SchemaDataType? DataType { get; init; }
}

public sealed class SchemaField : SchemaNode
{
    public SchemaField()
    {
        Kind = SchemaNodeKind.Field;
    }

    public IReadOnlyList<SchemaProcessorRef> Processors => ProcessorsList;
    internal List<SchemaProcessorRef> ProcessorsList { get; init; } = [];
    public required SchemaDataType DataType { get; init; }

    /// <summary>
    /// Optional constant (a literal parsed by <see cref="DataType"/>) or a reference to another value that this field must have.
    /// The writer supplies the value when the model holds none; the reader fails when the value read differs.
    /// The value compared is the logical value (after the semantic processors).
    /// The logical source/sink models are only called when the serializer was built with <see cref="SerializerBuilder.UseModelForValueFields"/> enabled; otherwise the value is only used for comparison.
    /// </summary>
    public SchemaValueOrRef<string> Value { get; init; }

    /// <summary>
    /// Optional physical length (in bytes) of the value (a constant or a reference to a value).
    /// Without a length a <c>sys.bytes</c> field takes the rest of the enclosing sized group (size window).
    /// The writer derives a referenced length field from the encoded byte count of the model value, not from its logical length (e.g. characters).
    /// </summary>
    public SchemaValueOrRef<int> ByteLength { get; init; }
}

public class SchemaGroup : SchemaNode
{
    public SchemaGroup()
    {
        Kind = SchemaNodeKind.Group;
    }

    public IReadOnlyList<SchemaProcessorRef> Processors => ProcessorsList;
    public List<SchemaProcessorRef> ProcessorsList { get; init; } = [];
    public IReadOnlyList<SchemaNode> Members => MemberList;
    internal List<SchemaNode> MemberList { get; init; } = [];

    /// <summary>
    /// Optional size in bytes of the encoded content of this group (a constant or a reference to a value).
    /// The reader limits the group to that window; the writer derives the value from the encoded content.
    /// </summary>
    public SchemaValueOrRef<int> ByteSize { get; init; }
}

public sealed class SchemaRepeat : SchemaGroup
{
    public SchemaRepeat()
    {
        Kind = SchemaNodeKind.Repeat;
    }

    public SchemaValueOrRef<int> Count { get; init; }

    /// <summary>
    /// Semantic (value) processors that convert the referenced count value into the (int) count.
    /// </summary>
    public IReadOnlyList<SchemaProcessorRef> ValueProcessors => ValueProcessorsList;
    internal List<SchemaProcessorRef> ValueProcessorsList { get; init; } = [];
}

public sealed class SchemaChoice : SchemaGroup
{
    public SchemaChoice()
    {
        Kind = SchemaNodeKind.Choice;
    }

    public required SchemaValueOrRef<int> SelectedIndex { get; init; }

    /// <summary>
    /// Semantic (value) processors that convert the referenced value into the (int) selected index.
    /// </summary>
    public IReadOnlyList<SchemaProcessorRef> ValueProcessors => ValueProcessorsList;
    internal List<SchemaProcessorRef> ValueProcessorsList { get; init; } = [];
}

public readonly union SchemaObject(SchemaField, SchemaGroup, SchemaRepeat, SchemaChoice);

public class Schema : SchemaGroup
{
    public Schema()
    {
        Kind = SchemaNodeKind.Schema;
    }

    public required IReadOnlyList<SchemaTypeDef> TypeDefs { get; init; }
    public required IReadOnlyList<SchemaProcessorDef> ProcessorDefs { get; init; }

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

public closed class SchemaProcessor
{
    public IReadOnlyList<SchemaProperty> Properties => PropertyList;
    internal List<SchemaProperty> PropertyList { get; init; } = [];
}

/// <summary>
/// A named processor declaration in the ProcessorDefs of a schema (document):
/// a processor key with default properties that can be referenced (by name) with a <see cref="SchemaProcessorRef"/>.
/// </summary>
public sealed class SchemaProcessorDef : SchemaProcessor
{
    /// <summary>The name used to reference this definition ('ref:name', or 'ref:document.name' from another document).</summary>
    public required string Name { get; init; }
    public required ProcessorKey Processor { get; init; }
}

/// <summary>
/// The use of a processor on a node or typedef: either a processor key ('namespace.id')
/// or a reference to a <see cref="SchemaProcessorDef"/> ('ref:name' or 'ref:document.name').
/// The properties of the ref override the properties of the definition.
/// </summary>
public sealed class SchemaProcessorRef : SchemaProcessor
{
    public required SchemaProcessorName Processor { get; init; }

    /// <summary>
    /// Optional namespace ('pubns') the processor publishes its values under, so published values do not collide.
    /// Not a processor property; it is interpreted by the engine. Null: the processor uses its own key as namespace.
    /// </summary>
    public string? PublishNamespace { get; init; }

    /// <summary>Filled when a reference is resolved (compiled).</summary>
    public SchemaProcessorDef? Definition { get; internal set; }

    /// <summary>The processor to use. Null when a reference is not (yet) resolved or a name has no namespace.</summary>
    public ProcessorKey? Key
        => Processor.IsReference
            ? Definition?.Processor
            : String.IsNullOrEmpty(Processor.Namespace) ? null : Processor.ToProcessorKey();

    /// <summary>The properties of the definition (if any) overridden by the properties of this ref.</summary>
    public IReadOnlyList<SchemaProperty> EffectiveProperties
    {
        get
        {
            if (Definition is null)
            {
                return Properties;
            }

            var merged = new List<SchemaProperty>(Definition.Properties);
            foreach (var property in Properties)
            {
                merged.RemoveAll(p => p.Name == property.Name);
                merged.Add(property);
            }
            return merged;
        }
    }
}

/// <summary>A constant, a reference to a field value ('ref:') or a reference to a published value ('pub:').</summary>
public union SchemaValueOrRef<T>(SchemaNodeRef, SchemaPubRef, T) { }

public sealed class SchemaProperty
{
    public required string Name { get; init; }
    /// <summary>
    /// The value-string is interpeted/parsed by the processor that uses this property.
    /// It may be a literal value or a reference to a processor that provides the value.
    /// Null means the property has no value (json null); a processor may give that a meaning (e.g. the default option of a map).
    /// </summary>
    public required string? Value { get; init; }
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
    /// <summary>A group of repeated fields and/or groups.</summary>
    Repeat,
    /// <summary>A choice between multiple fields or groups.</summary>
    Choice,
}
