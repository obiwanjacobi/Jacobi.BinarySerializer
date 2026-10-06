using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Execution;

// Adapt the flat field API to the scope based interfaces the sessions drive.
// Groups, repeat items and choices all share the one flat scope; the engine decides what is visited.

internal sealed class FieldSourceAdapter(IFieldSource source) : IValueSource
{
    public SourceResult GetField(FieldContext context) => source.GetField(context);

    public IValueSource EnterGroup(GroupContext context) => this;
    public IValueSource EnterItem(RepeatContext context, int index) => this;
    public IValueSource EnterChoice(ChoiceContext context) => this;

    // TODO: counts and selected indexes come from published values (or derived by the engine), not from a flat model.
    public int GetCount(RepeatContext context)
        => throw new NotSupportedException($"'{context.Path}': a flat field source cannot provide a repeat count.");

    public int GetSelectedIndex(ChoiceContext context)
        => throw new NotSupportedException($"'{context.Path}': a flat field source cannot provide a choice index.");
}

internal sealed class FieldSinkAdapter(IFieldSink sink) : IValueSink
{
    public void SetField(FieldContext context, LogicalField value) => sink.SetField(context, value);

    public IValueSink EnterGroup(GroupContext context) => this;
    public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
    public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
    public void Complete() { }
}
