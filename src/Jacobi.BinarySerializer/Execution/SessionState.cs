using System.Runtime.InteropServices;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Maintains the state of a processing session, including the pipeline and context.
/// </summary>
public closed class SessionState
{
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private string _direction = String.Empty;
    private readonly Dictionary<ProcessorBinding, ILogger> _processorLoggers = [];

    /// <summary>
    /// The data types of the plan being executed (set by the session).
    /// </summary>
    internal DataTypeRegistry DataTypes { get; set; } = DataTypeRegistry.CreateDefault();

    /// <summary>
    /// The logger of the engine itself (category 'Jacobi.BinarySerializer.Engine').
    /// </summary>
    protected internal ILogger EngineLogger { get; private set; } = NullLogger.Instance;

    /// <summary>
    /// Resolves the optional <see cref="ILoggerFactory"/> from the host's services (no-op logging when absent).
    /// </summary>
    internal void InitializeLogging(IServiceProvider services, string direction)
    {
        _direction = direction;
        _loggerFactory = (ILoggerFactory?)services.GetService(typeof(ILoggerFactory)) ?? NullLoggerFactory.Instance;
        EngineLogger = _loggerFactory.CreateLogger("Jacobi.BinarySerializer.Engine");
    }

    internal ILogger GetLogger(ProcessorBinding binding, ProcessorContext context)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_processorLoggers, binding, out _);
        return slot ??= new ContextLogger(_loggerFactory.CreateLogger(binding.LogCategory), context, binding, _direction);
    }

    // private processor state
    private readonly record struct PrivateKey(ProcessorBinding Owner, Type StateType, InstancePath Instance);
    private readonly Dictionary<PrivateKey, object> _private = [];

    internal T GetOrCreate<T>(ProcessorBinding owner, InstancePath instance = default) where T : class, new()
        => GetOrCreate<T>(owner, instance, out _);

    internal T GetOrCreate<T>(ProcessorBinding owner, InstancePath instance, out bool exists) where T : class, new()
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_private, new PrivateKey(owner, typeof(T), instance), out exists);
        return (T)(slot ??= new T());
    }

    // shared processor state
    private readonly Dictionary<PublishedValueKey, object?> _published = [];

    /// <summary>
    /// Publishes a public value (e.g. to prefill a repeat count before writing); a later publication of the same key overwrites the earlier one.
    /// </summary>
    public void Publish(PublishedValueKey key, object? value) => _published[key] = value;

    /// <summary>Publishes a public value by namespace and name ('pubns.name').</summary>
    public void Publish(string ns, string name, object? value) => Publish(new PublishedValueKey(ns, name), value);

    /// <summary>Publishes the value of the field at a schema path.</summary>
    public void Publish(SchemaPath path, object? value) => Publish(PublishedValueKey.ForPath(path), value);

    /// <summary>Publishes the value of the field at a schema path for one instance of its repeats.</summary>
    public void Publish(SchemaPath path, InstancePath instance, object? value) => Publish(PublishedValueKey.ForPath(path, instance), value);

    /// <summary>
    /// Resolves the count/index of a repeat or choice: the constant or published value, converted by the value processors of the node (if any).
    /// The processors always run in the read direction: the referenced value is the input, the int the output.
    /// </summary>
    internal int Resolve(ValueSource<int> source, GroupInfo node, ValueProcessorContext context, InstancePath current = default)
    {
        if (node.ValueProcessors.Count == 0)
        {
            return Resolve(source, node.Path, current);
        }

        object? value;
        if (source is int constant)
        {
            value = constant;
        }
        else if (source is PublishedValueKey declared)
        {
            value = ResolvePublished(declared, node.Path, current);
        }
        else
        {
            throw EngineLogger.Fail($"'{node.Path}': the value source is unresolved.");
        }

        context.Field = null!;
        context.Group = node;
        try
        {
            var logical = new LogicalField(node.Name, value?.GetType() ?? typeof(object), value);
            foreach (var binding in node.ValueProcessors)
            {
                context.Current = binding;
                context.ProcessorProperties = binding.Properties;
                logical = ((IValueProcessor)binding.Processor).Read(logical, context);
            }
            value = logical.Value;
        }
        finally
        {
            context.Group = null;
        }

        try
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw EngineLogger.Fail($"'{node.Path}': the processed value ({value ?? "null"}) is not an integer.", ex);
        }
    }

    /// <summary>Resolves a constant or a published value to an int. An unpublished value is a runtime error.</summary>
    /// <param name="current">The instance of the referring node; replaces the '[]' markers of the reference.</param>
    internal int Resolve(ValueSource<int> source, SchemaPath referrer, InstancePath current = default)
    {
        if (source is int constant)
        {
            return constant;
        }

        if (source is PublishedValueKey declared)
        {
            var value = ResolvePublished(declared, referrer, current);
            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw EngineLogger.Fail($"'{referrer}': the value '{declared}' ({value ?? "null"}) is not an integer.", ex);
            }
        }

        throw EngineLogger.Fail($"'{referrer}': the value source is unresolved.");
    }

    /// <summary>Gets a published value. An unpublished value is a runtime error.</summary>
    internal object? ResolvePublished(PublishedValueKey declared, SchemaPath referrer, InstancePath current = default)
    {
        var key = declared with { Instance = declared.Instance.ResolveRelative(current) };
        if (!_published.TryGetValue(key, out var value))
        {
            throw EngineLogger.Fail($"'{referrer}': the value '{key}' was not published (yet).");
        }
        return value;
    }

    /// <summary>
    /// Gets the value a field must have (its constant, or the published value it refers to), converted to the CLR type of the field.
    /// </summary>
    internal object? ResolveExpected(FieldInfo field, InstancePath current)
    {
        var expected = field.ValueReference is { } key ? ResolvePublished(key, field.Path, current) : field.ConstantValue;
        var clrType = field.DataType.ClrType;
        if (expected is null || clrType.IsInstanceOfType(expected))
        {
            return expected;
        }

        try
        {
            return Convert.ChangeType(expected, clrType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw EngineLogger.Fail($"'{field.Path}': the value '{expected}' cannot be converted to {field.DataType}.", ex);
        }
    }

    /// <summary>Fails when <paramref name="actual"/> differs from the value the field must have.</summary>
    internal void CheckExpected(FieldInfo field, object? actual, InstancePath current)
    {
        var expected = ResolveExpected(field, current);
        if (!(expected is byte[] expectedBytes && actual is byte[] actualBytes ? expectedBytes.AsSpan().SequenceEqual(actualBytes) : Equals(expected, actual)))
        {
            throw EngineLogger.Fail($"'{field.Path}': the value is '{actual ?? "null"}' but the schema requires '{expected ?? "null"}'.");
        }
    }
}
