using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer;

/// <summary>
/// Describes the current step for a value model: the plan node plus whatever the engine adds for that step.
/// Value models receive these instead of the plan's <see cref="NodeInfo"/> types, so extra information
/// and services can be added without changing the <see cref="IValueSource"/> and <see cref="IValueSink"/> signatures.
/// </summary>
public closed class ValueContext
{
    /// <summary>The plan node this step is about.</summary>
    public required NodeInfo Node { get; init; }

    public string Name => Node.Name;
    public string Path => Node.Path;

    public required IServiceProvider Services { get; init; }

    /// <summary>The repeat instance indices that lead to this node (empty outside repeats).</summary>
    public InstancePath Instance { get; init; }
}

public sealed class FieldContext : ValueContext
{
    public FieldInfo Field => (FieldInfo)Node;
}

public sealed class GroupContext : ValueContext
{
    public GroupInfo Group => (GroupInfo)Node;
}

public sealed class RepeatContext : ValueContext
{
    public RepeatInfo Repeat => (RepeatInfo)Node;
}

public sealed class ChoiceContext : ValueContext
{
    public ChoiceInfo Choice => (ChoiceInfo)Node;
}

/// <summary>
/// Describes an open choice: a choice without alternatives in the schema, only a nodeDef contract the chosen group must conform to.
/// </summary>
public sealed class OpenChoiceContext : ValueContext
{
    public ChoiceInfo Choice => (ChoiceInfo)Node;

    /// <summary>
    /// The nodeDef (contract) the selected group must conform to.
    /// </summary>
    public SchemaName? MemberNodeDef => ((SchemaChoice)Choice.Group).MemberNodeDef;
}

/// <summary>
/// Describes the next item of an open repeat (a repeat without a count).
/// <see cref="ValueContext.Instance"/> is the instance of the repeat itself; <see cref="Index"/> is the item the decision is for.
/// </summary>
public class OpenRepeatContext : ValueContext
{
    public RepeatInfo Repeat => (RepeatInfo)Node;

    /// <summary>The index of the item the decision is for (also the number of items so far).</summary>
    public required int Index { get; init; }
}

/// <summary>
/// Describes the next item of an open repeat while reading. It gives a view of the input that is left,
/// so the model can decide on a terminator or sentinel before that item is read.
/// </summary>
public sealed class OpenRepeatReadContext : OpenRepeatContext
{
    /// <summary>The physical bytes left before the readable end (enclosing size windows and reserved layout tails taken into account).</summary>
    public required long Remaining { get; init; }

    /// <summary>True when the engine's own rule would end the repeat here (what <see cref="OpenRepeatAction.Default"/> does).</summary>
    public required bool AtDefaultEnd { get; init; }

    /// <summary>The unread physical input up to the readable end; null when peeking is not reliable.</summary>
    internal ReadOnlySequence<byte>? Input { get; init; }

    /// <summary>
    /// Peeks at the next physical bytes without consuming them, bounded by the readable end.
    /// Returns false when peeking is not reliable at this point (a layout processor holds buffered input for the repeat's scope).
    /// Fewer bytes than asked means the end of the data (the message is complete).
    /// </summary>
    public bool TryPeek(int count, out ReadOnlySequence<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Input is not { } input)
        {
            bytes = default;
            return false;
        }
        bytes = input.Slice(0, Math.Min(count, input.Length));
        return true;
    }
}
