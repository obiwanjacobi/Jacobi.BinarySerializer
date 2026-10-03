using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Processor;

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
    /// Gets the value of a child field of this scope.
    /// Returns false when the model has no value for the field; the engine then derives it (length, count, ...) or fails.
    /// </summary>
    bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? value);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSource EnterGroup(GroupContext context);

    /// <summary>Gets the number of items the model holds for a child repeat.</summary>
    int GetCount(RepeatContext context);

    /// <summary>Enters one item of a child repeat. The scope holds the repeat's children.</summary>
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
    /// <summary>Sets the value of a child field. Fields the model does not map are ignored.</summary>
    void SetField(FieldContext context, LogicalField value);

    /// <summary>Enters a child group of this scope.</summary>
    IValueSink EnterGroup(GroupContext context);

    /// <summary>Enters one item of a child repeat. <paramref name="count"/> is the resolved item count.</summary>
    IValueSink EnterItem(RepeatContext context, int index, int count);

    /// <summary>Enters a child choice with the resolved alternative index.</summary>
    IValueSink EnterChoice(ChoiceContext context, int selectedIndex);

    /// <summary>
    /// Called when the scope is exhausted, on the scope that was returned by an Enter method (or on the root).
    /// Models that build immutable/typed instances create the instance here and hand it to their parent.
    /// </summary>
    void Complete();
}
