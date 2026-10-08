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
    private readonly ConcurrentDictionary<SchemaName, Lazy<ExecutionPlan>> _plans = new();

    internal Serializer(SchemaSet schemas, IProcessorProvider processorProvider, IServiceProvider? serviceProvider = null, DataTypeRegistry? dataTypes = null)
    {
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _processorProvider = processorProvider ?? throw new ArgumentNullException(nameof(processorProvider));
        _serviceProvider = serviceProvider;
        _dataTypes = dataTypes ?? DataTypeRegistry.CreateDefault();
    }

    /// <summary>
    /// Returns the cached plan for the schema; it is built once, thread-safe.
    /// </summary>
    public ExecutionPlan GetPlan(SchemaName schemaName)
    {
        return _plans.GetOrAdd(schemaName,
            name => new Lazy<ExecutionPlan>(() => ExecutionPlan.Create(_schemas, name, _processorProvider, _dataTypes))).Value;
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

    public void Serialize(SchemaName schemaName, IValueSource valueSource, IBufferWriter<byte> writer)
    {
        var plan = GetPlan(schemaName);
        var session = new WriterSession(plan, writer, _serviceProvider);
        session.Write(valueSource);
    }

    public void Serialize(ExecutionPlan plan, IValueSource valueSource, IBufferWriter<byte> writer)
    {
        var session = new WriterSession(plan, writer, _serviceProvider);
        session.Write(valueSource);
    }

    public void Serialize(PlanRange planRange, IFieldSource valueSource, IBufferWriter<byte> writer)
    {
        var session = new WriterSession(planRange.ExecutionPlan, writer, _serviceProvider);
        session.Write(valueSource, planRange);
    }

    public void Deserialize(SchemaName schemaName, ReadOnlySequence<byte> reader, IValueSink valueSink)
    {
        var plan = GetPlan(schemaName);
        var session = new ReaderSession(plan, _serviceProvider);
        session.Read(reader, valueSink);
    }

    public void Deserialize(ExecutionPlan plan, ReadOnlySequence<byte> reader, IValueSink valueSink)
    {
        var session = new ReaderSession(plan, _serviceProvider);
        session.Read(reader, valueSink);
    }
}
