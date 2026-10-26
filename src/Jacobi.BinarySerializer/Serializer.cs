using System.Buffers;
using System.Collections.Concurrent;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer;

public sealed class Serializer
{
    private readonly SchemaSet _schemas;
    private readonly IProcessorProvider _processorProvider;
    private readonly IServiceProvider? _serviceProvider;
    private readonly DataTypeRegistry _dataTypes;
    private readonly bool _valueFieldsUseModel;
    // by the group instance: a plan can be created for any group of the schemas
    private readonly ConcurrentDictionary<SchemaGroup, Lazy<ExecutionPlan>> _plans = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The compiled (frozen) schemas the serializer creates its plans from.
    /// </summary>
    public SchemaSet Schemas => _schemas;

    internal Serializer(SchemaSet schemas, IProcessorProvider processorProvider, IServiceProvider? serviceProvider = null, DataTypeRegistry? dataTypes = null, bool valueFieldsUseModel = false)
    {
        _valueFieldsUseModel = valueFieldsUseModel;
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _processorProvider = processorProvider ?? throw new ArgumentNullException(nameof(processorProvider));
        _serviceProvider = serviceProvider;
        _dataTypes = dataTypes ?? DataTypeRegistry.CreateDefault();
    }

    /// <summary>
    /// Returns the cached plan for the schema; it is built once, thread-safe.
    /// </summary>
    public ExecutionPlan GetPlan(SchemaName schemaName)
        => GetPlan(_schemas.FindRoot(schemaName));

    /// <summary>
    /// Returns the cached plan for any group of the <see cref="Schemas"/>: a root, a node def or a group nested in one of them.
    /// It is built once, thread-safe.
    /// </summary>
    /// <exception cref="ArgumentException">The group is not part of the <see cref="Schemas"/>.</exception>
    public ExecutionPlan GetPlan(SchemaGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _plans.GetOrAdd(group,
            key => new Lazy<ExecutionPlan>(() => ExecutionPlan.Create(_schemas, key, _processorProvider, _dataTypes))).Value;
    }

    /// <summary>
    /// Builds (and caches) the plan up front so errors surface early.
    /// </summary>
    public void Prepare(SchemaName schemaName) => GetPlan(schemaName);

    internal int CachedPlanCount => _plans.Count;

    /// <summary>
    /// Builds (and caches) the plans for all roots of all schema documents.
    /// </summary>
    public void PrepareAll()
    {
        foreach (var document in _schemas.Documents)
        {
            foreach (var root in document.Roots)
            {
                Prepare(new SchemaName($"{document.Name}{SchemaName.Separator}{root.Name}"));
            }
        }
    }

    public long Serialize(SchemaName schemaName, IValueSource valueSource, IBufferWriter<byte> writer)
    {
        var plan = GetPlan(schemaName);
        var session = new WriterSession(plan, writer, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Write(valueSource), session.BytesWritten);
    }

    public long Serialize(ExecutionPlan plan, IValueSource valueSource, IBufferWriter<byte> writer)
    {
        var session = new WriterSession(plan, writer, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Write(valueSource), session.BytesWritten);
    }

    public long Serialize(PlanRange planRange, IValueSource valueSource, IBufferWriter<byte> writer)
    {
        var session = new WriterSession(planRange.ExecutionPlan, writer, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Write(valueSource, planRange), session.BytesWritten);
    }

    public long Serialize(PlanRange planRange, IFieldSource valueSource, IBufferWriter<byte> writer)
    {
        var session = new WriterSession(planRange.ExecutionPlan, writer, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Write(valueSource, planRange), session.BytesWritten);
    }

    public long Deserialize(SchemaName schemaName, ReadOnlySequence<byte> reader, IValueSink valueSink)
    {
        var plan = GetPlan(schemaName);
        var session = new ReaderSession(plan, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Read(reader, valueSink), session.BytesRead);
    }

    public long Deserialize(ExecutionPlan plan, ReadOnlySequence<byte> reader, IValueSink valueSink)
    {
        var session = new ReaderSession(plan, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Read(reader, valueSink), session.BytesRead);
    }

    public long Deserialize(PlanRange planRange, ReadOnlySequence<byte> reader, IValueSink valueSink)
    {
        var session = new ReaderSession(planRange.ExecutionPlan, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Read(reader, valueSink, planRange), session.BytesRead);
    }

    public long Deserialize(PlanRange planRange, ReadOnlySequence<byte> reader, IFieldSink fieldSink)
    {
        var session = new ReaderSession(planRange.ExecutionPlan, _serviceProvider, _valueFieldsUseModel);
        return Complete(session.Read(reader, fieldSink, planRange), session.BytesRead);
    }

    // truncated input (or a full output) is an error: callers must supply a complete message.
    private static long Complete(ReadResult result, long bytes)
        => result == ReadResult.NeedMoreData
            ? throw new InvalidOperationException("The input is incomplete: more data is needed to complete the read.")
            : bytes;

    private static long Complete(WriteResult result, long bytes)
        => result switch
        {
            WriteResult.NeedMoreData => throw new InvalidOperationException("More data is needed to complete the write."),
            WriteResult.NeedMoreSpace => throw new InvalidOperationException("More space is needed in the output to complete the write."),
            _ => bytes,
        };
}
