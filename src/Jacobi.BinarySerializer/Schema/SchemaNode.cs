namespace Jacobi.BinarySerializer.Schema;

public closed class SchemaNode
{
    public required string Name { get; init; }
    public SchemaNodeKind Kind { get; internal init; }
    public IReadOnlyList<SchemaProperty> Properties => PropertyList;
    internal List<SchemaProperty> PropertyList { get; init; } = [];
    /// <summary>
    /// Optional reference to a reusable node in the schema's nodeDefs collection.
    /// </summary>
    public SchemaName? NodeDef { get; init; }
}

/// <summary>
/// A data type defined in a schema (document): a base data type with facets and processors.
/// It inherits the parser and the default representation of its base.
/// Referenced by a field with its full name ('document.name'); without a namespace it must be defined in the schema it is used in.
/// </summary>
public sealed class SchemaDataTypeDef : SchemaProcessor
{
    public required string Name { get; init; }

    /// <summary>The data type this type is based on: a built-in type or another data type def.</summary>
    public required SchemaName BasedOn { get; init; }

    /// <summary>The smallest allowed logical value (inclusive): a literal of the <see cref="ValueType"/>.</summary>
    public string? Min { get; init; }
    /// <summary>The largest allowed logical value (inclusive): a literal of the <see cref="ValueType"/>.</summary>
    public string? Max { get; init; }
    public decimal? Scale { get; init; }
    public decimal? Shift { get; init; }

    /// <summary>
    /// The allowed physical values mapped to their logical values (both literals).
    /// The physical values are parsed with this data type, the logical values with <see cref="ValueType"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>
    /// The data type of the value the value model sees (the logical value): a built-in type or another data type def.
    /// Without it: 'sys.string' with <see cref="Options"/>, 'sys.decimal' with a <see cref="Scale"/> or <see cref="Shift"/>,
    /// otherwise the value type of <see cref="BasedOn"/>.
    /// </summary>
    public SchemaName? ValueType { get; init; }

    /// <summary>
    /// The logical value (a literal of the value type) read for a physical value that is not one of the options.
    /// Without a default such a value is an error. The default has no physical value, so it cannot be written.
    /// </summary>
    public string? OptionsDefault { get; init; }

    /// <summary>Processors merged into every field that uses this data type (those of the base come first).</summary>
    public IReadOnlyList<SchemaProcessorRef> Processors { get; init; } = [];
}

public sealed class SchemaField : SchemaNode
{
    public SchemaField()
    {
        Kind = SchemaNodeKind.Field;
    }

    public IReadOnlyList<SchemaProcessorRef> Processors => ProcessorsList;
    internal List<SchemaProcessorRef> ProcessorsList { get; init; } = [];
    /// <summary>
    /// The logical data type of the field: a built-in type or a data type def.
    /// Optional when the field references a nodeDef that defines the data type.
    /// </summary>
    public SchemaName? DataType { get; init; }

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

    /// <summary>
    /// Optional signed offset in physical bytes, relative to the current read position, where the field is read from.
    /// A field with an offset is virtual: it is read at the offset and the position is restored afterwards; when writing no bytes are written.
    /// The model sees the value on read and must provide it on write (it is published for references).
    /// </summary>
    public int? ByteOffset { get; init; }
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

    public SchemaValueOrRef<int> SelectedIndex { get; init; }

    /// <summary>
    /// The nodeDef (a group) that the alternatives of this choice must conform to (the contract of an open choice).
    /// </summary>
    public SchemaName? MemberNodeDef { get; init; }

    /// <summary>
    /// Open choice only: whether the selected group inherits the processor pipeline of this choice (integrate) or not (isolate).
    /// </summary>
    public SchemaPipelineMode Pipeline
    {
        get => PipelineOverride ?? SchemaPipelineMode.Integrate;
        init => PipelineOverride = value;
    }

    /// <summary>
    /// The explicitly authored pipeline mode, or null to inherit it from a node definition.
    /// </summary>
    public SchemaPipelineMode? PipelineOverride { get; init; }

    /// <summary>
    /// Semantic (value) processors that convert the referenced value into the (int) selected index.
    /// </summary>
    public IReadOnlyList<SchemaProcessorRef> ValueProcessors => ValueProcessorsList;
    internal List<SchemaProcessorRef> ValueProcessorsList { get; init; } = [];
}

/// <summary>
/// How the group selected by an open choice relates to the processor pipeline of the choice.
/// </summary>
public enum SchemaPipelineMode
{
    /// <summary>The selected group inherits the processor stages of the choice (and so of its ancestors), like a nested group (default).</summary>
    Integrate = 0,
    /// <summary>The selected group has its own pipeline: processors of the choice and its ancestors do not apply.</summary>
    Isolate,
}

internal static class SchemaPipelineModes
{
    public static SchemaPipelineMode Parse(string? text, string choiceName)
    {
        if (String.IsNullOrEmpty(text))
        {
            return SchemaPipelineMode.Integrate;
        }
        if (Enum.TryParse<SchemaPipelineMode>(text, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }
        throw new InvalidOperationException(
            $"The Choice '{choiceName}' has an unknown pipeline mode '{text}' (use 'isolate' or 'integrate').");
    }
}

public enum SchemaNodeKind
{
    /// <summary>Root container object for a schema.</summary>
    Schema,
    /// <summary>A field within a group.</summary>
    Field,
    /// <summary>A group of fields.</summary>
    Group,
    /// <summary>A group of repeated fields and/or groups.</summary>
    Repeat,
    /// <summary>A choice between multiple fields or groups.</summary>
    Choice,
}
