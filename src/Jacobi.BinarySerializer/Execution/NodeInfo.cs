using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Execution;

public closed class NodeInfo
{
    public GroupInfo? Parent { get; internal set; }
    public int Index { get; internal set; }
    public required string Name { get; init; }
    public required SchemaPath Path { get; init; }

    public required ProcessorPipeline Pipeline { get; init; }
}

/// <summary>
/// Represents all the information available for a field.
/// </summary>
public sealed class FieldInfo : NodeInfo
{
    // schema field
    public required SchemaField Field { get; init; }

    /// <summary>
    /// The descriptor of the data type of the field, resolved from the registry.
    /// Its physical part (CLR type, default representation, size) describes the encoded value; its facets the transformation to the logical value.
    /// </summary>
    public required DataTypeDescriptor DataType { get; init; }

    /// <summary>
    /// The data type of the value the value model sees and provides (the logical value): <see cref="DataTypeDescriptor.ValueType"/> of <see cref="DataType"/>.
    /// The engine converts the value to and from it at the boundary with the value model.
    /// Null when the field declares its own semantic processors (e.g. 'sys.map'): then their output is passed as is.
    /// </summary>
    public DataTypeDescriptor? ValueType { get; init; }

    /// <summary>The type of the values the engine supplies for the field (constants, counts, lengths): <see cref="ValueType"/>, or <see cref="DataType"/> without one.</summary>
    internal DataTypeDescriptor ModelType => ValueType ?? DataType;

    /// <summary>True when another node refers to this field's value by schema path; the session publishes it.</summary>
    public bool PublishesValue { get; internal set; }

    /// <summary>
    /// The sibling repeat whose count refers to this field. When the model holds no value for the field, the writer derives it from the repeat's item count.
    /// </summary>
    public RepeatInfo? CountOf { get; internal set; }

    /// <summary>
    /// The sibling group whose size refers to this field. When the model holds no value for the field, the writer derives it from the encoded size of that group.
    /// </summary>
    public GroupInfo? ByteSizeOf { get; internal set; }

    /// <summary>
    /// The physical length in bytes of the encoded field, for a data type that supports a length (e.g. <c>sys.bytes</c>, <c>sys.string</c>).
    /// Unset when the field has no length (a data type that takes the rest of the enclosing size window does so).
    /// </summary>
    public ValueSource<int> ByteLength { get; init; }

    public bool HasByteLength => ByteLength is int or PublishedValueKey;

    /// <summary>
    /// Signed offset in physical bytes from the current read position where the field is read.
    /// A field with an offset is virtual: read at the offset with the position restored afterwards; when writing the model must provide the value, but no bytes are written.
    /// </summary>
    public int? ByteOffset { get; init; }

    /// <summary>
    /// The sibling field whose length (in encoded bytes) refers to this field. When the model holds no value for the field, the writer derives it from the byte length of that field's value (currently only <c>byte[]</c>).
    /// </summary>
    public FieldInfo? ByteLengthOf { get; internal set; }

    /// <summary>The parsed constant of <see cref="SchemaField.Value"/> (null when the field has no constant).</summary>
    public object? ConstantValue { get; internal set; }

    /// <summary>The published value that <see cref="SchemaField.Value"/> refers to (null when the field has no reference).</summary>
    public PublishedValueKey? ValueReference { get; internal set; }

    /// <summary>True when the field has a constant or a referenced value that it must have.</summary>
    public bool HasExpectedValue => ConstantValue is not null || ValueReference is not null;
}

public class GroupInfo : NodeInfo
{
    // schema group
    public required SchemaGroup Group { get; init; }
    public required IReadOnlyList<NodeInfo> Members { get; init; }

    /// <summary>
    /// Semantic processors that convert the referenced value of a repeat count or choice index into the int (empty for other groups).
    /// </summary>
    public IReadOnlyList<ProcessorBinding> ValueProcessors { get; init; } = [];

    /// <summary>The data type of the field the count/index refers to (null for constants and published values).</summary>
    public DataTypeDescriptor? ValueType { get; internal set; }

    /// <summary>
    /// The size in bytes of the encoded content of the group (unset when the group has no size).
    /// </summary>
    public ValueSource<int> ByteSize { get; init; }

    public bool HasByteSize => ByteSize is int or PublishedValueKey;
}

public sealed class RepeatInfo : GroupInfo
{
    public ValueSource<int> Count { get; init; }

    /// <summary>
    /// True when the schema gives no count: the repeat runs until the end of the input.
    /// </summary>
    public bool UntilEnd => Count is not (int or PublishedValueKey);

    /// <summary>
    /// True when the engine can end a repeat without a count by itself: it has a size, or nothing follows it up to the end of the input or of an enclosing size window.
    /// Otherwise (an open repeat followed by other nodes) the value model must end it on read (<see cref="IOpenRepeatSink"/>).
    /// </summary>
    public bool EndsAtInputEnd => UntilEnd && (HasByteSize || IsLast(this));

    private static bool IsLast(NodeInfo node)
        => node.Parent is not { } parent
            || (parent is ChoiceInfo || node.Index == parent.Members.Count - 1) && (parent.HasByteSize || IsLast(parent));
}

public sealed class ChoiceInfo : GroupInfo
{
    /// <summary>
    /// True for an open choice: it has no alternatives in the schema, the value model selects the group (see <see cref="SchemaChoice.MemberNodeDef"/>).
    /// </summary>
    public bool IsOpen => ((SchemaChoice)Group).MemberNodeDef is not null;

    public required ValueSource<int> SelectedIndex { get; init; }
}

public readonly union ValueSource<T>(T, PublishedValueKey);

/// <summary>
/// Identifies a public value: a processor-published 'pubns.name', or the value of a field addressed by its schema path.
/// </summary>
public readonly record struct PublishedValueKey(string Namespace, string Name, InstancePath Instance = default)
{
    public static PublishedValueKey ForPath(SchemaPath path, InstancePath instance = default) => new(string.Empty, path.Value, instance);

    public override string ToString() => string.IsNullOrEmpty(Namespace) ? $"{Name}{Instance}" : $"{Namespace}/{Name}{Instance}";
}
