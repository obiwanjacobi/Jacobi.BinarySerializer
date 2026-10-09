using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using Microsoft.Extensions.Logging;

namespace Jacobi.BinarySerializer.Processor;

public enum StateScope
{
    /// <summary>One state instance per processor binding, shared by all repeat iterations.</summary>
    Binding,
    /// <summary>One state instance per repeat instance (the current <see cref="ProcessorContext.Instance"/> path).</summary>
    Instance
}

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

    /// <summary>
    /// Scoped lookup (full 'ns:id.name' names, short-name fallback) over the processor's own properties.
    /// </summary>
    public ProcessorProperties Properties
        => new(ProcessorProperties, Current is null ? null : Current.Processor.Key, _state.DataTypes);

    /// <summary>
    /// Scoped lookup over other properties (e.g. field or group properties) for the current processor.
    /// </summary>
    public ProcessorProperties PropertiesOf(IReadOnlyList<SchemaProperty>? properties)
        => new(properties ?? [], Current is null ? null : Current.Processor.Key, _state.DataTypes);

    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// The data types known to the serializer (to resolve a data type name to its descriptor).
    /// </summary>
    public IDataTypeRegistry DataTypes => _state.DataTypes;

    /// <summary>
    /// Logger for the current processor (category identifies the processor); no-op when the host configured no logging.
    /// </summary>
    public ILogger Logger => _state.GetLogger(Current, this);

    /// <summary>The schema path of the node being processed (for the log scope).</summary>
    internal virtual string NodePath => String.Empty;

    /// <summary>The message position (layout stage only; for the log scope).</summary>
    internal virtual long? ScopeRootPosition => null;

    /// <summary>The group position (layout stage only; for the log scope).</summary>
    internal virtual long? ScopeGroupPosition => null;

    /// <summary>
    /// The repeat instance indices that lead to the current node (empty outside repeats; set by the session before each call).
    /// </summary>
    public InstancePath Instance { get; internal set; }

    // allow processors to store arbitrary state in the context
    // - they cannot read each other's state
    internal ProcessorBinding Current { get; set; } = null!;   // set by the session before each call
    public T GetOrCreateState<T>(StateScope scope = StateScope.Binding) where T : class, new()
        => _state.GetOrCreate<T>(Current, scope == StateScope.Instance ? Instance : default);

    /// <summary>
    /// Same as <see cref="GetOrCreateState{T}(StateScope)"/>; returns true when the state already existed (false when it was just created).
    /// </summary>
    public bool GetOrCreateState<T>(StateScope scope, out T state) where T : class, new()
    {
        state = _state.GetOrCreate<T>(Current, scope == StateScope.Instance ? Instance : default, out var exists);
        return exists;
    }

    /// <summary>
    /// Publishes a value under <paramref name="name"/>. 
    /// The namespace is the full name of the current processor (e.g. 'sys.crc'),
    /// unless the schema set a 'pubns' on the processor: that namespace is used instead.
    /// </summary>
    public void Publish(string name, object? value)
        => _state.Publish(Current.PublishNamespace ?? Current.Processor.Key.ToString(), name, value);

    // TODO: consume dynamic values published by other processors.
}

public sealed class ValueProcessorContext : ProcessorContext
{
    public ValueProcessorContext(SessionState state) : base(state) { }

    internal override string NodePath => Field?.Path.ToString() ?? Group?.Path.ToString() ?? String.Empty;

    /// <summary>
    /// The field being processed (set by the session before each call).
    /// Null when a repeat count or choice index is processed: see <see cref="Group"/>.
    /// </summary>
    public FieldInfo Field { get; internal set; } = null!;

    /// <summary>The repeat or choice whose count/index value is being processed (null when processing a field).</summary>
    public GroupInfo? Group { get; internal set; }

    /// <summary>The data type of the value being processed (null when unknown).</summary>
    public DataTypeDescriptor? DataType => Field is not null ? Field.DataType : Group?.ValueType;
}

public sealed class FieldProcessorContext : ProcessorContext
{
    public FieldProcessorContext(SessionState state) : base(state) { }

    internal override string NodePath => Field?.Path.ToString() ?? String.Empty;

    /// <summary>
    /// The field being processed (set by the session before each call).
    /// </summary>
    public FieldInfo Field { get; internal set; } = null!;

    /// <summary>
    /// The running data of the field being processed (set by the session before each call).
    /// </summary>
    public FieldData FieldData { get; } = new();
}

public sealed class LayoutProcessorContext : ProcessorContext
{
    public LayoutProcessorContext(SessionState state) : base(state) { }

    internal override string NodePath => (Field?.Path ?? Group?.Path)?.ToString() ?? String.Empty;
    internal override long? ScopeRootPosition => GroupData.RootPosition;
    internal override long? ScopeGroupPosition => GroupData.Position;

    /// <summary>
    /// The group being laid out (set by the session before each call).
    /// </summary>
    public GroupInfo Group { get; internal set; } = null!;

    /// <summary>
    /// The field whose encoded value is being written/read: its metadata (data type, properties) describes the value.
    /// Null for the group-level calls (Begin/End).
    /// </summary>
    public FieldInfo? Field { get; internal set; }

    /// <summary>
    /// The running data of the field whose encoded value is being written/read (set by the session before each field call).
    /// <see cref="FieldData.Length"/> is the number of bytes the reader must take for a field with a length.
    /// </summary>
    public FieldData FieldData { get; } = new();

    /// <summary>
    /// The running data of the group being laid out (set by the session before each call).
    /// </summary>
    public GroupData GroupData { get; } = new();
}

public sealed class StreamProcessorContext : ProcessorContext
{
    public StreamProcessorContext(SessionState state) : base(state) { }

    internal override string NodePath => Group?.Path.ToString() ?? String.Empty;

    /// <summary>The group whose payload is being processed (set by the session before each call).</summary>
    public GroupInfo Group { get; internal set; } = null!;
}
