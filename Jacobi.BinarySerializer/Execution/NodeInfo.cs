using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Execution;

public closed class NodeInfo
{
    public GroupInfo? Parent { get; internal set; }
    public int Index { get; internal set; }
    public required string Name { get; init; }
    public required string Path { get; init; }

    public required ProcessorPipeline Pipeline { get; init; }
}

/// <summary>
/// Represents all the information available for a field.
/// </summary>
public sealed class FieldInfo : NodeInfo
{
    // schema field
    public required SchemaField Field { get; init; }

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