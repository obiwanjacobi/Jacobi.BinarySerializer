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
// - Reading and writing walk the same sequence: every scope is entered and exited, the exit is called on the scope
//   the enter returned. The root model is the scope of the root node: it is not entered, but it is exited.
//   A group:  EnterGroup ... ExitGroup.
//   A repeat: EnterRepeat (EnterOpenRepeat for a repeat without a count), then per item EnterItem ... ExitItem on
//             the repeat's scope, then ExitRepeat.
//   A choice: EnterChoice (EnterOpenChoice for a choice without a selectedIndex), the selected member through the
//             choice's scope with the calls above, then ExitChoice.

/// <summary>
/// Provides the values to write. An instance represents one scope (group, repeat, repeat item or choice).
/// </summary>
public interface IValueSource
{
    /// <summary>
    /// Gets the value of a field.
    /// Answers NoValue when the model has no value for the field (the engine then derives it or fails),
    /// or EndOfData on the first field of an item of a repeat without a count when there are no more items
    /// (the engine's own rule, see <see cref="OpenRepeatAction.Default"/>).
    /// </summary>
    SourceResult GetField(FieldContext context);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSource EnterGroup(GroupContext context);

    /// <summary>Called when a group scope is done, on the scope that was returned by EnterGroup (or on the root).</summary>
    void ExitGroup(GroupContext context);

    /// <summary>
    /// Enters a child repeat with a count: <paramref name="count"/> is the resolved number of items. The scope holds its items
    /// (EnterItem is called on it). A repeat without a count is entered with <see cref="IOpenRepeatSource.EnterOpenRepeat"/>.
    /// </summary>
    IValueSource EnterRepeat(RepeatContext context, int count);

    /// <summary>
    /// Called when a repeat is done, on the scope that was returned by EnterRepeat or EnterOpenRepeat (or on the root).
    /// <paramref name="count"/> is the number of items.
    /// </summary>
    void ExitRepeat(RepeatContext context, int count);

    /// <summary>
    /// Enters one item of a repeat, on the scope of the repeat. The scope holds the repeat's members.
    /// <paramref name="count"/> is the number of items, or -1 for a repeat that is written item by item.
    /// </summary>
    IValueSource EnterItem(RepeatContext context, int index, int count);

    /// <summary>Called when an item is done, on the scope that was returned by EnterItem.</summary>
    void ExitItem(RepeatContext context, int index, int count);

    /// <summary>
    /// Enters a child choice with the resolved alternative index. The scope holds the selected alternative.
    /// A choice without a selectedIndex is entered with <see cref="IOpenChoiceSource.EnterOpenChoice"/>.
    /// </summary>
    IValueSource EnterChoice(ChoiceContext context, int selectedIndex);

    /// <summary>
    /// Called when a choice is done, on the scope that was returned by EnterChoice or EnterOpenChoice.
    /// <paramref name="selectedIndex"/> is the index of the alternative, -1 for a body of a choice with a memberNodeDef.
    /// </summary>
    void ExitChoice(ChoiceContext context, int selectedIndex);
}

/// <summary>
/// Receives the values that were read. An instance represents one scope (group, repeat, repeat item or choice).
/// </summary>
public interface IValueSink
{
    /// <summary>Sets the value of a field. Fields the model does not map are ignored.</summary>
    void SetField(FieldContext context, LogicalField value);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSink EnterGroup(GroupContext context);

    /// <summary>
    /// Called when a group scope is done, on the scope that was returned by EnterGroup (or on the root).
    /// Models that build immutable/typed instances create the instance here and hand it to their parent.
    /// </summary>
    void ExitGroup(GroupContext context);

    /// <summary>
    /// Enters a child repeat: <paramref name="count"/> is the resolved number of items, or -1 for a repeat without a
    /// count when this scope is not an <see cref="IOpenRepeatSink"/> (the engine's own rule ends it). The scope receives
    /// its items (EnterItem is called on it).
    /// </summary>
    IValueSink EnterRepeat(RepeatContext context, int count);

    /// <inheritdoc cref="IValueSource.ExitRepeat"/>
    void ExitRepeat(RepeatContext context, int count);

    /// <summary>
    /// Enters one item of a repeat, on the scope of the repeat. The scope receives the repeat's members.
    /// <paramref name="count"/> is the number of items, or -1 for a repeat that is read item by item.
    /// </summary>
    IValueSink EnterItem(RepeatContext context, int index, int count);

    /// <inheritdoc cref="IValueSource.ExitItem"/>
    void ExitItem(RepeatContext context, int index, int count);

    /// <summary>
    /// Enters a child choice with the resolved alternative index. The scope receives the selected alternative.
    /// A choice without a selectedIndex is entered with <see cref="IOpenChoiceSink.EnterOpenChoice"/>.
    /// </summary>
    IValueSink EnterChoice(ChoiceContext context, int selectedIndex);

    /// <inheritdoc cref="IValueSource.ExitChoice"/>
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
    /// The scope of the choice, as <see cref="IValueSource.EnterChoice"/> returns it: the engine handles the selected
    /// member through it with the usual calls (EnterGroup for a group or a body, GetField for a field, EnterRepeat,
    /// EnterChoice or EnterOpenChoice) and calls ExitChoice on it when the choice is done.
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
    /// Selects the member of a child open choice and enters the choice: in place of <see cref="IValueSource.EnterChoice"/>.
    /// </summary>
    OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context);
}

/// <summary>
/// Optional extension of <see cref="IValueSink"/> for models that support open choices.
/// </summary>
public interface IOpenChoiceSink : IValueSink
{
    /// <summary>
    /// Selects the member of a child open choice (based on values already seen) and enters the choice: in place of
    /// <see cref="IValueSink.EnterChoice"/>.
    /// </summary>
    OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context);
}

/// <summary>
/// The scope of an open repeat to write, and its number of items when the model knows it. Made with
/// <see cref="Counted"/> or <see cref="ItemByItem"/>.
/// </summary>
public readonly record struct OpenRepeatSourceResult
{
    private OpenRepeatSourceResult(IValueSource scope, int count)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Count = count;
    }

    /// <summary>The scope of the repeat, as <see cref="IValueSource.EnterRepeat"/> returns it.</summary>
    public IValueSource Scope { get; }

    /// <summary>The number of items, or -1: item by item (the engine asks NextItem before each item).</summary>
    public int Count { get; }

    /// <summary>The model knows the number of items.</summary>
    public static OpenRepeatSourceResult Counted(IValueSource scope, int count)
        => new(scope, count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count), count, "The number of items cannot be negative."));

    /// <summary>
    /// The model decides per item: the engine asks <see cref="IOpenRepeatSource.NextItem"/> on the scope before each item
    /// (the engine's own rule when the scope is not an <see cref="IOpenRepeatSource"/>).
    /// </summary>
    public static OpenRepeatSourceResult ItemByItem(IValueSource scope) => new(scope, -1);

    /// <summary>The same result with another scope (e.g. a scope that wraps this one).</summary>
    public OpenRepeatSourceResult WithScope(IValueSource scope) => new(scope, Count);
}

/// <summary>
/// The scope of an open repeat to read, and its number of items when the model knows it. Made with
/// <see cref="Counted"/> or <see cref="ItemByItem"/>.
/// </summary>
public readonly record struct OpenRepeatSinkResult
{
    private OpenRepeatSinkResult(IValueSink scope, int count)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Count = count;
    }

    /// <summary>The scope of the repeat, as <see cref="IValueSink.EnterRepeat"/> returns it.</summary>
    public IValueSink Scope { get; }

    /// <inheritdoc cref="OpenRepeatSourceResult.Count"/>
    public int Count { get; }

    /// <inheritdoc cref="OpenRepeatSourceResult.Counted"/>
    public static OpenRepeatSinkResult Counted(IValueSink scope, int count)
        => new(scope, count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count), count, "The number of items cannot be negative."));

    /// <summary>
    /// The model decides per item: the engine asks <see cref="IOpenRepeatSink.NextItem"/> on the scope before each item
    /// (the engine's own rule when the scope is not an <see cref="IOpenRepeatSink"/>).
    /// </summary>
    public static OpenRepeatSinkResult ItemByItem(IValueSink scope) => new(scope, -1);

    /// <inheritdoc cref="OpenRepeatSourceResult.WithScope"/>
    public OpenRepeatSinkResult WithScope(IValueSink scope) => new(scope, Count);
}

/// <summary>
/// The value model's decision for the next item of an open repeat (a repeat without a count).
/// </summary>
public enum OpenRepeatAction
{
    /// <summary>
    /// Let the engine decide with its own rule: on read the end of the input or size window; on write the first field
    /// of the next item answers EndOfData (asked on the scope of the repeat).
    /// </summary>
    Default,
    /// <summary>There is another item: enter it.</summary>
    Next,
    /// <summary>No more items: end the repeat and continue with the next sibling.</summary>
    End,
}

/// <summary>
/// Optional extension of <see cref="IValueSource"/> for models that write repeats without a count. Required on the scope
/// that holds such a repeat; also for a repeat whose count field the engine derives from the items written (the model
/// has no value for it).
/// </summary>
public interface IOpenRepeatSource : IValueSource
{
    /// <summary>
    /// Enters a child repeat without a count (on the scope that holds it): in place of <see cref="IValueSource.EnterRepeat"/>.
    /// </summary>
    OpenRepeatSourceResult EnterOpenRepeat(RepeatContext context);

    /// <summary>
    /// Called on the scope of an open repeat that is written item by item, before each item (including the first), to
    /// decide if the item <paramref name="index"/> (also the number of items so far) is written.
    /// </summary>
    OpenRepeatAction NextItem(RepeatContext context, int index);
}

/// <summary>
/// Optional extension of <see cref="IValueSink"/> for models that decide how many items a repeat without a count has.
/// Without it the engine's own rule (the end of the input or size window) ends such a repeat.
/// </summary>
public interface IOpenRepeatSink : IValueSink
{
    /// <summary>
    /// Enters a child repeat without a count (on the scope that holds it): in place of <see cref="IValueSink.EnterRepeat"/>.
    /// </summary>
    OpenRepeatSinkResult EnterOpenRepeat(OpenRepeatReadContext context);

    /// <summary>
    /// Called on the scope of an open repeat that is read item by item, before each item (including the first), to
    /// decide if the item <paramref name="index"/> (also the number of items so far) is read.
    /// The model has already received the values of the previous items, so it can stop after a terminating item (e.g. PNG IEND, MIDI End of Track)
    /// or, by peeking, before a terminator that a sibling node reads (e.g. a 0x00 end marker).
    /// </summary>
    OpenRepeatAction NextItem(OpenRepeatReadContext context, int index);
}
