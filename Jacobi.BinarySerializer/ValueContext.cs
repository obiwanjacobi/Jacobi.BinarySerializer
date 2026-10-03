using Jacobi.BinarySerializer.Execution;

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
