namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Running (per call) data of the field being processed. The plan metadata of the field is in <see cref="Execution.FieldInfo"/>.
/// Set by the session before each call; read-only for processors.
/// </summary>
public sealed class FieldData
{
    /// <summary>
    /// The resolved length in bytes of the field (a constant, or a referenced/published value that is now known).
    /// Null when the field declares no byte length.
    /// </summary>
    public int? ByteLength { get; internal set; }

    /// <summary>
    /// The most bytes the default layout read offers to a field processor that decides its own width.
    /// The session grows it when the processor needs more data.
    /// </summary>
    public int OpenWidthWindowBytes { get; internal set; } = DefaultLayoutProcessor.OpenWidthWindowBytes;
}

/// <summary>
/// Running (per call) data of the group being processed. The plan metadata of the group is in <see cref="Execution.GroupInfo"/>.
/// Set by the session before each call; read-only for processors.
/// </summary>
public sealed class GroupData
{
    /// <summary>
    /// The resolved size in bytes of the group.
    /// When reading: known when the group starts. When writing: only known (non-null) when the group ends.
    /// Null when the group declares no size (or the size is not known yet).
    /// </summary>
    public int? ByteSize { get; internal set; }

    /// <summary>Bytes written/read since the start of the message (the layout payload), at the start of the current call.</summary>
    public long RootPosition { get; internal set; }

    /// <summary>Bytes written/read since the start of the group that owns the layout (see <see cref="RootPosition"/> for the whole message), at the start of the current call.</summary>
    public long Position { get; internal set; }

    /// <summary>
    /// The zero-based index of the repeat item being laid out. Null when the group is not a repeat item.
    /// </summary>
    public int? RepeatIndex { get; internal set; }

    /// <summary>
    /// The number of items of the repeat. Null when the group is not a repeat item or the count is not known (repeat until the end).
    /// </summary>
    public int? RepeatCount { get; internal set; }

    /// <summary>
    /// The index of the selected alternative of the choice. Null when the group is not a choice.
    /// </summary>
    public int? ChoiceIndex { get; internal set; }

    internal void SetNode(Execution.GroupInfo group, int? itemIndex, int? itemCount, int? choiceIndex)
    {
        RepeatIndex = group is Execution.RepeatInfo ? itemIndex : null;
        RepeatCount = group is Execution.RepeatInfo && itemCount >= 0 ? itemCount : null;
        ChoiceIndex = group is Execution.ChoiceInfo ? choiceIndex : null;
    }
}
