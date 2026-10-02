using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Represents all the information available for a field.
/// </summary>
public sealed class FieldInfo
{
    // schema parent
    // schema field
    public required SchemaGroup Parent { get; init; }
    public required SchemaField Field { get; init; }

    // codecs (objects implementing IProcessor)
    public required IReadOnlyList<IProcessor> Processors { get; init; }

    // parent GroupInfo
    // previous FieldInfo
    // next FieldInfo
}


public sealed class GroupInfo
{
    // schema parent
    // schema group
    // codecs (objects implementing IProcessor)

    // parent GroupInfo
    // previous GroupInfo
    // next GroupInfo
}