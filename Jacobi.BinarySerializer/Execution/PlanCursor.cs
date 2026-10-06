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
    /// <summary>One item of a repeat is about to be entered. The caller must create its scope and call <see cref="PlanCursor{TScope}.Enter"/>.</summary>
    EnterItem,
    /// <summary>One item of a repeat is exhausted (and popped).</summary>
    ExitItem,
}

/// <param name="Kind">What to do.</param>
/// <param name="Node">The plan node the step is about (null for Done).</param>
/// <param name="Scope">
/// EnterGroup: the scope of the parent group (default for the root).
/// Field: the scope of the group that contains the field.
/// ExitGroup: the scope of the group that was exited (for a repeat: the scope of its parent).
/// EnterItem: the scope of the group that contains the repeat.
/// ExitItem: the scope of the item that was exited.
/// </param>
/// <param name="Index">EnterItem/ExitItem: the zero-based item index.</param>
/// <param name="Count">EnterItem/ExitItem: the number of items of the repeat.</param>
internal readonly record struct CursorStep<TScope>(CursorStepKind Kind, NodeInfo? Node, TScope? Scope, int Index = 0, int Count = 0);

/// <summary>
/// Walks an <see cref="ExecutionPlan"/> with an explicit frame stack (no recursion, no call-stack state),
/// so a read or write can be suspended and resumed. Used for both writing and reading.
/// The cursor is model-agnostic: the caller decides what a scope is (an <see cref="IValueSource"/> or <see cref="IValueSink"/>).
/// </summary>
internal sealed class PlanCursor<TScope>
{
    private sealed class Frame(GroupInfo group, TScope scope, int firstChild, int endChild)
    {
        public GroupInfo Group { get; } = group;
        public TScope Scope { get; } = scope;
        public int NextChild { get; set; } = firstChild;
        public int EndChild { get; } = endChild;

        /// <summary>Repeat frame: the number of items (null for group, choice and item frames).</summary>
        public int? Count { get; set; }
        /// <summary>Repeat frame: the item count is not known up front (until the end of the input).</summary>
        public bool Open { get; init; }
        /// <summary>Repeat frame: the index of the current item. Item frame: its own index.</summary>
        public int Iteration { get; set; } = -1;
        public bool IsItem { get; init; }
    }

    private readonly Stack<Frame> _frames = new();
    private readonly PlanRange? _range;
    private GroupInfo? _pendingEnter;
    private bool _pendingAnnounced;
    private int _pendingItemIndex = -1;

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

    /// <summary>The instance indices of the repeat items the cursor is currently inside (outermost first).</summary>
    public InstancePath Instance
    {
        get
        {
            var path = InstancePath.Empty;
            foreach (var frame in _frames.Reverse())
            {
                if (frame.IsItem)
                {
                    path = path.Append(frame.Iteration);
                }
            }
            return path;
        }
    }

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
        if (frame.Count is { } count)
        {
            if (_range is not null)
            {
                var above = Instance;
                while (frame.Iteration + 1 < count && !_range.Includes(frame.Group, above.Append(frame.Iteration + 1)))
                {
                    frame.Iteration++;
                }
            }

            if (frame.Iteration + 1 < count)
            {
                frame.Iteration++;
                _pendingEnter = frame.Group;
                _pendingAnnounced = true;
                _pendingItemIndex = frame.Iteration;
                return new(CursorStepKind.EnterItem, frame.Group, frame.Scope, frame.Iteration, frame.Open ? -1 : count);
            }

            _frames.Pop();
            return new(CursorStepKind.ExitGroup, frame.Group, frame.Scope);
        }

        if (_range is not null)
        {
            var instance = Instance;
            while (frame.NextChild < frame.EndChild && !_range.Includes(frame.Group.Children[frame.NextChild], instance))
            {
                frame.NextChild++;
            }
        }

        if (frame.NextChild < frame.EndChild)
        {
            var child = frame.Group.Children[frame.NextChild++];
            switch (child)
            {
                case FieldInfo:
                    return new(CursorStepKind.Field, child, frame.Scope);

                case RepeatInfo:
                case GroupInfo:
                    _pendingEnter = (GroupInfo)child;
                    _pendingAnnounced = true;
                    return new(CursorStepKind.EnterGroup, child, frame.Scope);

                default:
                    throw new NotSupportedException($"'{child.Path}': unsupported node type {child.GetType().Name}.");
            }
        }

        _frames.Pop();
        return frame.IsItem
            ? new(CursorStepKind.ExitItem, frame.Group, frame.Scope, frame.Iteration, _frames.Peek().Open ? -1 : _frames.Peek().Count ?? 0)
            : new(CursorStepKind.ExitGroup, frame.Group, frame.Scope);
    }

    /// <summary>
    /// Completes an <see cref="CursorStepKind.EnterGroup"/> step: pushes the frame with the scope of the entered group.
    /// </summary>
    /// <param name="scope">The scope of the entered group.</param>
    /// <param name="selectedIndex">Required for a choice: the index of the one alternative to visit.</param>
    public void Enter(TScope scope, int? selectedIndex = null)
    {
        if (_pendingEnter is null || !_pendingAnnounced)
        {
            throw new InvalidOperationException("Enter(scope) can only be called after an EnterGroup step.");
        }

        if (_pendingEnter is RepeatInfo && _pendingItemIndex < 0)
        {
            throw new InvalidOperationException($"'{_pendingEnter.Path}': use EnterRepeat(scope, count) to enter a repeat.");
        }

        var first = 0;
        var end = _pendingEnter.Children.Count;
        if (_pendingEnter is ChoiceInfo)
        {
            if (selectedIndex is not { } index || index < 0 || index >= end)
            {
                throw new InvalidOperationException(
                    $"'{_pendingEnter.Path}': choice index {selectedIndex?.ToString() ?? "(none)"} is out of range (0..{end - 1}).");
            }
            first = index;
            end = index + 1;
        }

        _frames.Push(new Frame(_pendingEnter, scope, first, end)
        {
            IsItem = _pendingItemIndex >= 0,
            Iteration = _pendingItemIndex,
        });
        _pendingEnter = null;
        _pendingAnnounced = false;
        _pendingItemIndex = -1;
    }

    /// <summary>
    /// Completes an <see cref="CursorStepKind.EnterGroup"/> step for a repeat: the cursor will emit an EnterItem step per item.
    /// </summary>
    /// <param name="parentScope">The scope of the group that contains the repeat (the items are entered from it).</param>
    /// <param name="count">The resolved number of items; 0 skips the repeat.</param>
    public void EnterRepeat(TScope parentScope, int count)
    {
        if (_pendingEnter is not RepeatInfo repeat || !_pendingAnnounced || _pendingItemIndex >= 0)
        {
            throw new InvalidOperationException("EnterRepeat can only be called after an EnterGroup step for a repeat.");
        }

        if (count < 0)
        {
            throw new InvalidOperationException($"'{repeat.Path}': the repeat count {count} cannot be negative.");
        }

        _frames.Push(new Frame(repeat, parentScope, 0, 0) { Count = count });
        _pendingEnter = null;
        _pendingAnnounced = false;
    }

    /// <summary>
    /// Completes an EnterGroup step for a repeat without a count: items are emitted until <see cref="CloseRepeat"/> is called.
    /// </summary>
    public void EnterOpenRepeat(TScope parentScope)
    {
        if (_pendingEnter is not RepeatInfo repeat || !_pendingAnnounced || _pendingItemIndex >= 0)
        {
            throw new InvalidOperationException("EnterOpenRepeat can only be called after an EnterGroup step for a repeat.");
        }

        _frames.Push(new Frame(repeat, parentScope, 0, 0) { Count = Int32.MaxValue, Open = true });
        _pendingEnter = null;
        _pendingAnnounced = false;
    }

    /// <summary>True when the next step is the start of an item (or the end) of a repeat without a count.</summary>
    public bool AtOpenRepeat => _pendingEnter is null && _frames.Count > 0 && _frames.Peek() is { Open: true };

    /// <summary>Ends the open repeat: no more items are emitted.</summary>
    public void CloseRepeat()
    {
        if (!AtOpenRepeat)
        {
            throw new InvalidOperationException("CloseRepeat can only be called at a repeat without a count.");
        }
        var frame = _frames.Peek();
        frame.Count = frame.Iteration + 1;
    }
}
