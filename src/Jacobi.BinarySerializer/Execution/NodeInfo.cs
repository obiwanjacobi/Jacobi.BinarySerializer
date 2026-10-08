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

    /// <summary>The descriptor of the logical data type of the field, resolved from the registry.</summary>
    public required DataTypeDescriptor DataType { get; init; }

    /// <summary>True when another node
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
}

public sealed class ChoiceInfo : GroupInfo
{
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
