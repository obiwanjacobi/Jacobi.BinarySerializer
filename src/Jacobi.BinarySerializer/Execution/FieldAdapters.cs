using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Execution;

// Adapt the flat field API to the scope based interfaces the sessions drive.
// Groups, repeat items and choices all share the one flat scope; the engine decides what is visited.

// A repeat without a count is written item by item with the engine's own rule: the first field of the next item
// answers EndOfData (OpenRepeatAction.Default).
internal sealed class FieldSourceAdapter(IFieldSource source) : IOpenRepeatSource
{
    public SourceResult GetField(FieldContext context) => source.GetField(context);

    public IValueSource EnterGroup(GroupContext context) => this;
    public void ExitGroup(GroupContext context) { }
    public IValueSource EnterRepeat(RepeatContext context, int count) => this;
    public OpenRepeatSourceResult EnterOpenRepeat(RepeatContext context) => OpenRepeatSourceResult.ItemByItem(this);
    public OpenRepeatAction NextItem(RepeatContext context, int index) => OpenRepeatAction.Default;
    public void ExitRepeat(RepeatContext context, int count) { }
    public IValueSource EnterItem(RepeatContext context, int index, int count) => this;
    public void ExitItem(RepeatContext context, int index, int count) { }
    public IValueSource EnterChoice(ChoiceContext context, int selectedIndex) => this;
    public void ExitChoice(ChoiceContext context, int selectedIndex) { }
}

internal sealed class FieldSinkAdapter(IFieldSink sink) : IValueSink
{
    public void SetField(FieldContext context, LogicalField value) => sink.SetField(context, value);

    public IValueSink EnterGroup(GroupContext context) => this;
    public void ExitGroup(GroupContext context) { }
    public IValueSink EnterRepeat(RepeatContext context, int count) => this;
    public void ExitRepeat(RepeatContext context, int count) { }
    public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
    public void ExitItem(RepeatContext context, int index, int count) { }
    public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
    public void ExitChoice(ChoiceContext context, int selectedIndex) { }
}
