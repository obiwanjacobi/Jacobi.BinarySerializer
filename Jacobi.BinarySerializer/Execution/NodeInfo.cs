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

    /// <summary>True when another node refers to this field's value by schema path; the session publishes it.</summary>
    public bool PublishesValue { get; internal set; }

    /// <summary>
    /// The sibling repeat whose count refers to this field. When the model holds no value for the field, the writer derives it from the repeat's item count.
    /// </summary>
    public RepeatInfo? CountOf { get; internal set; }

    /// <summary>
    /// The sibling group whose size refers to this field. When the model holds no value for the field, the writer derives it from the encoded size of that group.
    /// </summary>
    public GroupInfo? SizeOf { get; internal set; }

    /// <summary>
    /// The length in bytes of a <see cref="SchemaDataType.Bytes"/> field (unset when the field has no length: it takes the rest of the enclosing size window).
    /// </summary>
    public ValueSource<int> Length { get; init; }

    public bool HasLength => Length is int or PublishedValueKey;

    /// <summary>
    /// The sibling bytes field whose length refers to this field. When the model holds no value for the field, the writer derives it from the length of that field's value.
    /// </summary>
    public FieldInfo? LengthOf { get; internal set; }

    /// <summary>The parsed constant of <see cref="SchemaField.Value"/> (null when the field has no constant).</summary>
    public object? ConstantValue { get; internal set; }

    /// <summary>The published value that <see cref="SchemaField.Value"/> refers to (null when the field has no reference).</summary>
    public PublishedValueKey? ValueReference { get; internal set; }

    /// <summary>True when the field has a constant or a referenced value that it must have.</summary>
    public bool HasExpectedValue => ConstantValue is not null || ValueReference is not null;

    // previous FieldInfo
    // next FieldInfo
}

public class GroupInfo : NodeInfo
{
    // schema group
    public required SchemaGroup Group { get; init; }
    public required IReadOnlyList<NodeInfo> Children { get; init; }

    /// <summary>
    /// Semantic processors that convert the referenced value of a repeat count or choice index into the int (empty for other groups).
    /// </summary>
    public IReadOnlyList<ProcessorBinding> ValueProcessors { get; init; } = [];

    /// <summary>The data type of the field the count/index refers to (null for constants and published values).</summary>
    public SchemaDataType? ValueType { get; internal set; }

    /// <summary>
    /// The size in bytes of the encoded content of the group (unset when the group has no size).
    /// </summary>
    public ValueSource<int> Size { get; init; }

    public bool HasSize => Size is int or PublishedValueKey;

    // previous GroupInfo
    // next GroupInfo
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
/// Identifies a public value: a processor-published 'pubns/name', or the value of a field addressed by its schema path.
/// </summary>
public readonly record struct PublishedValueKey(string Namespace, string Name, InstancePath Instance = default)
{
    public static PublishedValueKey ForPath(SchemaPath path, InstancePath instance = default) => new(string.Empty, path.Value, instance);

    public override string ToString() => string.IsNullOrEmpty(Namespace) ? $"{Name}{Instance}" : $"{Namespace}/{Name}{Instance}";
}
