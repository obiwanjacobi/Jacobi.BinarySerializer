using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processor;

public closed class ProcessorContext
{
    private readonly SessionState _state;

    protected ProcessorContext(SessionState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    // the current pipeline stage for this context, which determines what processors are available
    public PipelineStage Stage { get; init; }

    /// <summary>
    /// Collected properties for the processor, including properties from the schema and any additional properties (TBD).
    /// </summary>
    public IReadOnlyList<SchemaProperty> ProcessorProperties { get; internal set; } = [];

    /// <summary>Scoped lookup (full 'ns:id.name' names, short-name fallback) over the processor's own properties.</summary>
    public ProcessorProperties Properties
        => new(ProcessorProperties, Current is null ? null : Current.Processor.Key);

    /// <summary>Scoped lookup over other properties (e.g. field or group properties) for the current processor.</summary>
    public ProcessorProperties PropertiesOf(IReadOnlyList<SchemaProperty>? properties)
        => new(properties ?? [], Current is null ? null : Current.Processor.Key);
    public required IServiceProvider Services { get; init; }

    /// <summary>The repeat instance indices that lead to the current node (empty outside repeats; set by the session before each call).</summary>
    public InstancePath Instance { get; internal set; }

    // allow processors to store arbitrary state in the context
    // - they cannot read each other's state
    internal ProcessorBinding Current { get; set; } = null!;   // set by the session before each call
    public T GetOrCreateState<T>() where T : class, new() => _state.GetOrCreate<T>(Current);

    // publish dynamic values for processors to use, e.g. a data-length value read from the message header.
    // consume dynamic values published by other processors.
    public void Publish(string ns, string key, object? value) => _state.Publish(ns, key, value);
}

public sealed class ValueProcessorContext : ProcessorContext
{
    public ValueProcessorContext(SessionState state) : base(state) { }

    /// <summary>The field being processed (set by the session before each call).</summary>
    public FieldInfo Field { get; internal set; } = null!;
}

public sealed class FieldProcessorContext : ProcessorContext
{
    public FieldProcessorContext(SessionState state) : base(state) { }

    /// <summary>The field being processed (set by the session before each call).</summary>
    public FieldInfo Field { get; internal set; } = null!;
}

public sealed class LayoutProcessorContext : ProcessorContext
{
    public LayoutProcessorContext(SessionState state) : base(state) { }

    /// <summary>The group being laid out (set by the session before each call).</summary>
    public GroupInfo Group { get; internal set; } = null!;

    /// <summary>
    /// The field whose encoded value is being written/read: its metadata (data type, properties) describes the value.
    /// Null for the group-level calls (Begin/End).
    /// </summary>
    public FieldInfo? Field { get; internal set; }

    /// <summary>Bytes written/read since the start of the message (the layout payload), at the start of the current call.</summary>
    public long RootPosition { get; internal set; }

    /// <summary>Bytes written/read since the start of the group that owns the layout (see RootPosition for the whole message).</summary>
    public long GroupPosition { get; internal set; }
}

public sealed class StreamProcessorContext : ProcessorContext
{
    public StreamProcessorContext(SessionState state) : base(state) { }

    /// <summary>The group whose payload is being processed (set by the session before each call).</summary>
    public GroupInfo Group { get; internal set; } = null!;
}
