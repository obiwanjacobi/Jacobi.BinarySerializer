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

    // previous FieldInfo
    // next FieldInfo
}

public class GroupInfo : NodeInfo
{
    // schema group
    public required SchemaGroup Group { get; init; }
    public required IReadOnlyList<NodeInfo> Children { get; init; }

    // previous GroupInfo
    // next GroupInfo
}

public sealed class RepeatInfo : GroupInfo
{
    public required ValueSource<int> Count { get; init; }
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
