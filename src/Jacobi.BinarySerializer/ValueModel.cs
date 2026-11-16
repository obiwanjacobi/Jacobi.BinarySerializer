using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer;

// The value model abstraction: the session walks the ExecutionPlan and talks to these interfaces,
// never to a concrete value model (generic tree, typed objects, ...).
//
// Contracts that keep other value models (e.g. typed objects) possible:
// - Scopes are plain objects that the session keeps on its frame stack (no call-stack state), so reads/writes can be resumed.
// - Steps are described by context objects (see ValueContext), which identify the plan node (Name/Path) and can grow.
// - A model need not map every schema field (length prefixes, counts, reserved bits): the engine derives those.

/// <summary>
/// Provides the values to write. An instance represents one scope (group, repeat item or choice).
/// </summary>
public interface IValueSource
{
    /// <summary>
    /// Gets the value of a field.
    /// Answers NoValue when the model has no value for the field (the engine then derives it or fails),
    /// or EndOfData on the first field of an item of a count-less repeat when there are no more items.
    /// </summary>
    SourceResult GetField(FieldContext context);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSource EnterGroup(GroupContext context);

    /// <summary>
    /// Gets the number of items the model holds for a child repeat.
    /// Answer -1 (unknown) for a count-less repeat: the engine then probes the first field of each item with GetField (on this source, with the item instance)
    /// until it answers EndOfData. Only flat models can do that.
    /// </summary>
    int GetCount(RepeatContext context);

    /// <summary>Enters one item of a child repeat. The scope holds the repeat's members.</summary>
    IValueSource EnterItem(RepeatContext context, int index);

    /// <summary>Enters a child choice. The scope holds the choice's alternatives.</summary>
    IValueSource EnterChoice(ChoiceContext context);
}

/// <summary>
/// Receives the values that were read. An instance represents one scope (group, repeat item or choice).
/// </summary>
public interface IValueSink
{
    /// <summary>Sets the value of a field. Fields the model does not map are ignored.</summary>
    void SetField(FieldContext context, LogicalField value);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSink EnterGroup(GroupContext context);

    /// <summary>Enters one item of a child repeat. <paramref name="count"/> is the resolved item count, or -1 for a repeat without a count (not known up front).</summary>
    IValueSink EnterItem(RepeatContext context, int index, int count);

    /// <summary>Enters a child choice with the resolved alternative index.</summary>
    IValueSink EnterChoice(ChoiceContext context, int selectedIndex);

    /// <summary>
    /// Called when a group scope is exhausted, on the scope that was returned by EnterGroup (or on the root).
    /// Models that build immutable/typed instances create the instance here and hand it to their parent.
    /// </summary>
    void ExitGroup(GroupContext context);

    /// <summary>
    /// Called when a repeat item scope is exhausted, on the scope that was returned by EnterItem.
    /// <paramref name="count"/> is the resolved item count, or -1 for a repeat without a count.
    /// </summary>
    void ExitItem(RepeatContext context, int index, int count);

    /// <summary>
    /// Called when a choice scope is exhausted, on the scope that was returned by EnterChoice.
    /// </summary>
    void ExitChoice(ChoiceContext context, int selectedIndex);
}

/// <summary>
/// The member the model selected for an open choice to write, and the scope that holds its values. Made with
/// <see cref="Select"/> (the whole member), <see cref="SelectNode"/> (one node inside it) or <see cref="SelectRange"/>
/// (a range inside it).
/// </summary>
public readonly record struct OpenChoiceSourceResult
{
    private OpenChoiceSourceResult(IValueSource scope, SchemaName member, OpenChoiceTarget target)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Member = OpenChoiceTarget.CheckMember(member);
        Target = target;
    }

    /// <summary>
    /// The scope the engine uses for the selected member: the member's own scope for a group, a choice or a body (the
    /// engine enters the member with it, without a further EnterGroup or EnterChoice); for a field or a repeat, the scope
    /// the engine calls GetField/SetField, or GetCount/EnterItem, on for that member. It may be a scope dedicated to the
    /// member. For a member that is an open choice itself, the engine selects its member with EnterOpenChoice on it.
    /// </summary>
    public IValueSource Scope { get; }

    /// <summary>
    /// The selected member: the name of one of the members of the choice, or, for a choice with a memberNodeDef, the
    /// group that conforms to it (a schema root: 'document.group', or its name when that is unique).
    /// </summary>
    public SchemaName Member { get; }

    /// <summary>Where the write begins inside the selected member (empty: the whole member).</summary>
    public SchemaPath Begin => Target.Begin;

    /// <summary>The item indices of the repeats around <see cref="Begin"/> inside the selected member (empty: from the first item).</summary>
    public InstancePath BeginInstance => Target.BeginInstance;

    /// <summary>Where the write ends inside the selected member, inclusive (empty: the end of <see cref="Begin"/>).</summary>
    public SchemaPath End => Target.End;

    /// <summary>
    /// The item indices of the repeats around <see cref="End"/> (empty: up to the last item; when <see cref="End"/> is
    /// empty as well, the items of <see cref="BeginInstance"/>).
    /// </summary>
    public InstancePath EndInstance => Target.EndInstance;

    private OpenChoiceTarget Target { get; }

    /// <summary>Selects the whole member (a group or a field).</summary>
    public static OpenChoiceSourceResult Select(IValueSource scope, SchemaName member)
        => new(scope, member, default);

    /// <summary>
    /// Selects one node inside a member that is a group: <paramref name="target"/> is its path, starting with the name of
    /// the member (e.g. 'BodyA.Part1.Sub'), <paramref name="instance"/> the item indices of the repeats around it inside
    /// the member (outermost first, e.g. [5] for 'BodyA.Items.Part').
    /// </summary>
    public static OpenChoiceSourceResult SelectNode(IValueSource scope, SchemaName member, SchemaPath target, InstancePath instance = default)
        => new(scope, member, OpenChoiceTarget.Node(target, instance));

    /// <summary>
    /// Selects the nodes from <paramref name="begin"/> up to <paramref name="end"/> (inclusive) inside a member that is
    /// a group: paths that start with the name of the member, with the item indices of the repeats around them inside
    /// the member (outermost first; empty: from the first item, up to the last item).
    /// </summary>
    public static OpenChoiceSourceResult SelectRange(IValueSource scope, SchemaName member,
        SchemaPath begin, InstancePath beginInstance, SchemaPath end, InstancePath endInstance)
        => new(scope, member, OpenChoiceTarget.Range(begin, beginInstance, end, endInstance));

    /// <summary>The same selection with another scope (e.g. a scope that wraps this one).</summary>
    public OpenChoiceSourceResult WithScope(IValueSource scope) => new(scope, Member, Target);
}

/// <summary>
/// The member the model selected for an open choice to read, and the scope that receives its values. Made with
/// <see cref="Select"/> (the whole member), <see cref="SelectNode"/> (one node inside it) or <see cref="SelectRange"/>
/// (a range inside it).
/// </summary>
public readonly record struct OpenChoiceSinkResult
{
    private OpenChoiceSinkResult(IValueSink scope, SchemaName member, OpenChoiceTarget target)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Member = OpenChoiceTarget.CheckMember(member);
        Target = target;
    }

    /// <inheritdoc cref="OpenChoiceSourceResult.Scope"/>
    public IValueSink Scope { get; }

    /// <inheritdoc cref="OpenChoiceSourceResult.Member"/>
    public SchemaName Member { get; }

    /// <summary>Where the read begins inside the selected member (empty: the whole member).</summary>
    public SchemaPath Begin => Target.Begin;

    /// <inheritdoc cref="OpenChoiceSourceResult.BeginInstance"/>
    public InstancePath BeginInstance => Target.BeginInstance;

    /// <summary>Where the read ends inside the selected member, inclusive (empty: the end of <see cref="Begin"/>).</summary>
    public SchemaPath End => Target.End;

    /// <inheritdoc cref="OpenChoiceSourceResult.EndInstance"/>
    public InstancePath EndInstance => Target.EndInstance;

    private OpenChoiceTarget Target { get; }

    /// <inheritdoc cref="OpenChoiceSourceResult.Select"/>
    public static OpenChoiceSinkResult Select(IValueSink scope, SchemaName member)
        => new(scope, member, default);

    /// <inheritdoc cref="OpenChoiceSourceResult.SelectNode"/>
    public static OpenChoiceSinkResult SelectNode(IValueSink scope, SchemaName member, SchemaPath target, InstancePath instance = default)
        => new(scope, member, OpenChoiceTarget.Node(target, instance));

    /// <inheritdoc cref="OpenChoiceSourceResult.SelectRange"/>
    public static OpenChoiceSinkResult SelectRange(IValueSink scope, SchemaName member,
        SchemaPath begin, InstancePath beginInstance, SchemaPath end, InstancePath endInstance)
        => new(scope, member, OpenChoiceTarget.Range(begin, beginInstance, end, endInstance));

    /// <inheritdoc cref="OpenChoiceSourceResult.WithScope"/>
    public OpenChoiceSinkResult WithScope(IValueSink scope) => new(scope, Member, Target);
}

/// <summary>
/// The nodes inside the selected member of an open choice that are read or written, shared by
/// <see cref="OpenChoiceSourceResult"/> and <see cref="OpenChoiceSinkResult"/>: empty for the whole member.
/// </summary>
internal readonly record struct OpenChoiceTarget(SchemaPath Begin, InstancePath BeginInstance, SchemaPath End, InstancePath EndInstance)
{
    public static OpenChoiceTarget Node(SchemaPath target, InstancePath instance)
        => new(CheckPath(target, nameof(target)), instance, default, default);

    public static OpenChoiceTarget Range(SchemaPath begin, InstancePath beginInstance, SchemaPath end, InstancePath endInstance)
        => new(CheckPath(begin, nameof(begin)), beginInstance, CheckPath(end, nameof(end)), endInstance);

    public static SchemaName CheckMember(SchemaName member)
        => String.IsNullOrEmpty(member.Name) ? throw new ArgumentException("The selected member has no name.", nameof(member)) : member;

    private static SchemaPath CheckPath(SchemaPath path, string name)
        => path.IsEmpty ? throw new ArgumentException("The target path is empty.", name) : path;
}

/// <summary>
/// Optional extension of <see cref="IValueSource"/> for models that support open choices.
/// </summary>
public interface IOpenChoiceSource : IValueSource
{
    /// <summary>
    /// Selects the member of a child open choice and enters its scope.
    /// </summary>
    OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context);
}

/// <summary>
/// Optional extension of <see cref="IValueSink"/> for models that support open choices.
/// </summary>
public interface IOpenChoiceSink : IValueSink
{
    /// <summary>
    /// Selects the member of a child open choice (based on values already seen) and enters its scope.
    /// </summary>
    OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context);
}

/// <summary>
/// The value model's decision for the next item of an open repeat (a repeat without a count).
/// </summary>
public enum OpenRepeatAction
{
    /// <summary>Let the engine decide with its own rules (end of data/window on read, GetCount/EndOfData probe on write).</summary>
    Default,
    /// <summary>There is another item: enter it.</summary>
    Next,
    /// <summary>No more items: end the repeat and continue with the next sibling.</summary>
    End,
}

/// <summary>
/// Optional extension of <see cref="IValueSource"/> for models that decide when an open repeat ends.
/// Implemented by the scope that holds the repeat (the parent scope, or the root for a root repeat).
/// </summary>
public interface IOpenRepeatSource : IValueSource
{
    /// <summary>
    /// Called before each item of a child open repeat (including the first) to decide if the item is written.
    /// The engine then does not ask <see cref="IValueSource.GetCount"/> for the repeat.
    /// </summary>
    OpenRepeatAction NextItem(OpenRepeatContext context);
}

/// <summary>
/// Optional extension of <see cref="IValueSink"/> for models that decide when an open repeat ends.
/// Implemented by the scope that holds the repeat (the parent scope, or the root for a root repeat).
/// </summary>
public interface IOpenRepeatSink : IValueSink
{
    /// <summary>
    /// Called before each item of a child open repeat (including the first) to decide if another item is read.
    /// The model has already received the values of the previous items, so it can stop after a terminating item (e.g. PNG IEND, MIDI End of Track)
    /// or, by peeking, before a terminator that a sibling node reads (e.g. a 0x00 end marker).
    /// </summary>
    OpenRepeatAction NextItem(OpenRepeatReadContext context);
}
