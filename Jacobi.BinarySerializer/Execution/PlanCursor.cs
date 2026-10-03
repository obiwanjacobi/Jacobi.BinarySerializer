using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Execution;

internal enum CursorStepKind
{
    /// <summary>All nodes were visited.</summary>
    Done,
    /// <summary>A group is about to be entered. The caller must create its scope and call <see cref="PlanCursor{TScope}.Enter"/>.</summary>
    EnterGroup,
    /// <summary>A field must be processed.</summary>
    Field,
    /// <summary>A group is exhausted (and popped).</summary>
    ExitGroup,
}

/// <param name="Kind">What to do.</param>
/// <param name="Node">The plan node the step is about (null for Done).</param>
/// <param name="Scope">
/// EnterGroup: the scope of the parent group (default for the root).
/// Field: the scope of the group that contains the field.
/// ExitGroup: the scope of the group that was exited.
/// </param>
internal readonly record struct CursorStep<TScope>(CursorStepKind Kind, NodeInfo? Node, TScope? Scope);

/// <summary>
/// Walks an <see cref="ExecutionPlan"/> with an explicit frame stack (no recursion, no call-stack state),
/// so a read or write can be suspended and resumed. Used for both writing and reading.
/// The cursor is model-agnostic: the caller decides what a scope is (an <see cref="IValueSource"/> or <see cref="IValueSink"/>).
/// </summary>
internal sealed class PlanCursor<TScope>
{
    private sealed class Frame(GroupInfo group, TScope scope)
    {
        public GroupInfo Group { get; } = group;
        public TScope Scope { get; } = scope;
        public int NextChild { get; set; }
    }

    private readonly Stack<Frame> _frames = new();
    private readonly PlanRange? _range;
    private GroupInfo? _pendingEnter;
    private bool _pendingAnnounced;

    /// <param name="root">The root group of the plan.</param>
    /// <param name="range">Optional: only the fields of the range (and the groups leading to them) are visited.</param>
    public PlanCursor(GroupInfo root, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _pendingEnter = root;
        _range = range;
    }

    /// <summary>Number of groups currently entered.</summary>
    public int Depth => _frames.Count;

    /// <summary>The group the cursor is currently in (null before the root is entered and after it is exited).</summary>
    public GroupInfo? CurrentGroup => _frames.Count > 0 ? _frames.Peek().Group : null;

    /// <summary>Advances to the next step.</summary>
    public CursorStep<TScope> Next()
    {
        if (_pendingEnter is not null)
        {
            if (_pendingAnnounced)
            {
                throw new InvalidOperationException(
                    $"Enter(scope) must be called for group '{_pendingEnter.Path}' before advancing the cursor.");
            }

            // announced the root
            _pendingAnnounced = true;
            return new(CursorStepKind.EnterGroup, _pendingEnter, default);
        }

        if (_frames.Count == 0)
        {
            return new(CursorStepKind.Done, null, default);
        }

        var frame = _frames.Peek();
        if (_range is not null)
        {
            while (frame.NextChild < frame.Group.Children.Count && !_range.Includes(frame.Group.Children[frame.NextChild]))
            {
                frame.NextChild++;
            }
        }

        if (frame.NextChild < frame.Group.Children.Count)
        {
            var child = frame.Group.Children[frame.NextChild++];
            switch (child)
            {
                case FieldInfo:
                    return new(CursorStepKind.Field, child, frame.Scope);

                // TODO: repeat (iterate children Count times) and choice (visit only the selected alternative).
                case RepeatInfo or ChoiceInfo:
                    throw new NotSupportedException($"'{child.Path}': {child.GetType().Name} is not supported by the cursor yet.");

                case GroupInfo:
                    _pendingEnter = (GroupInfo)child;
                    _pendingAnnounced = true;
                    return new(CursorStepKind.EnterGroup, child, frame.Scope);

                default:
                    throw new NotSupportedException($"'{child.Path}': unsupported node type {child.GetType().Name}.");
            }
        }

        _frames.Pop();
        return new(CursorStepKind.ExitGroup, frame.Group, frame.Scope);
    }

    /// <summary>Completes an <see cref="CursorStepKind.EnterGroup"/> step: pushes the frame with the scope of the entered group.</summary>
    public void Enter(TScope scope)
    {
        if (_pendingEnter is null || !_pendingAnnounced)
        {
            throw new InvalidOperationException("Enter(scope) can only be called after an EnterGroup step.");
        }

        _frames.Push(new Frame(_pendingEnter, scope));
        _pendingEnter = null;
        _pendingAnnounced = false;
    }
}
