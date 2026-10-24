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

    /// <summary>Gets the index of the choice alternative that the model holds for a child choice.</summary>
    int GetSelectedIndex(ChoiceContext context);

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
/// The group the model selected for an open choice and the scope that holds (or receives) its values.
/// </summary>
/// <param name="Scope">The scope for the values of the body.</param>
/// <param name="Group">The selected group (a schema root that conforms to the nodeDef of the open choice).</param>
/// <param name="Target">
/// Optional: the path of a group inside the selected group (starting with its root name, e.g. 'BodyA.Part1.Sub').
/// Only that group (and the groups leading to it) is read or written. Empty: the whole group.
/// </param>
/// <param name="TargetInstance">
/// Optional: the item indices of the repeats around <paramref name="Target"/> inside the selected group (outermost first),
/// e.g. [5] for 'BodyA.Items.Part' to read or write only item 5. Empty: all items.
/// </param>
public readonly record struct OpenChoiceSourceResult(IValueSource Scope, SchemaName Group, SchemaPath Target = default, InstancePath TargetInstance = default);

/// <summary>
/// <inheritdoc cref="OpenChoiceSourceResult"/>
/// </summary>
public readonly record struct OpenChoiceSinkResult(IValueSink Scope, SchemaName Group, SchemaPath Target = default, InstancePath TargetInstance = default);

/// <summary>
/// Optional extension of <see cref="IValueSource"/> for models that support open choices.
/// </summary>
public interface IOpenChoiceSource : IValueSource
{
    /// <summary>
    /// Selects the group for a child open choice and enters its scope.
    /// </summary>
    OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context);
}

/// <summary>
/// Optional extension of <see cref="IValueSink"/> for models that support open choices.
/// </summary>
public interface IOpenChoiceSink : IValueSink
{
    /// <summary>
    /// Selects the group for a child open choice (based on values already seen) and enters its scope.
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
