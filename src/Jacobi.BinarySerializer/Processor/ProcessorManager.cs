using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Descriptors;
using Microsoft.Extensions.DependencyInjection;

namespace Jacobi.BinarySerializer.Processor;

public interface IProcessorFactory
{
    string Namespace { get; }

    IProcessor? CreateProcessor(string id);

    /// <summary>
    /// The data types this factory's processors use for their properties.
    /// They are registered when the serializer is built; use names in the factory's own namespace.
    /// </summary>
    IEnumerable<DataTypeDescriptor> DataTypes => [];
}

public interface IProcessorFactoryProvider
{
    IProcessorFactory GetFactory(string @namespace);
    bool TryGetFactory(string @namespace, [NotNullWhen(true)] out IProcessorFactory? factory);
}

public interface IProcessorProvider
{
    IProcessor CreateProcessor(ProcessorKey key);
    bool TryCreateProcessor(ProcessorKey key, [NotNullWhen(true)] out IProcessor? processor);

    /// <summary>The data types published by the known processor factories.</summary>
    IEnumerable<DataTypeDescriptor> DataTypes => [];
}

public sealed class ProcessorManager : IProcessorProvider, IProcessorFactoryProvider
{
    private readonly Dictionary<string, IProcessorFactory> _processorFactories = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<DataTypeDescriptor> DataTypes => _processorFactories.Values.SelectMany(f => f.DataTypes);

    public IProcessor CreateProcessor(ProcessorKey key)
    {
        var factory = GetFactory(key.Namespace);
        var processor = factory.CreateProcessor(key.Id);
        return processor
            ?? throw new ArgumentException($"The Processor factory for '{key.Namespace} could not create processor '{key.Id}'.");
    }

    public bool TryCreateProcessor(ProcessorKey key, [NotNullWhen(true)] out IProcessor? processor)
    {
        if (TryGetFactory(key.Namespace, out var factory))
        {
            processor = factory.CreateProcessor(key.Id);
            return processor is not null;
        }
        processor = null;
        return false;
    }

    public IProcessorFactory GetFactory(string @namespace)
    {
        if (!TryGetFactory(@namespace, out var factory))
        {
            throw new KeyNotFoundException($"No processor factory registered for '{@namespace}'.");
        }
        return factory;
    }

    public bool TryGetFactory(string @namespace, [NotNullWhen(true)] out IProcessorFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(@namespace);
        return _processorFactories.TryGetValue(@namespace, out factory);
    }

    public void Register(IProcessorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_processorFactories.ContainsKey(factory.Namespace))
        {
            throw new InvalidOperationException($"A processor factory '{factory.Namespace}' is already registered.");
        }

        _processorFactories[factory.Namespace] = factory;
    }

    public bool LoadFromAssembly(string assemblyPath)
        => LoadFromAssembly(System.Reflection.Assembly.LoadFrom(assemblyPath));

    public bool LoadFromAssembly(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var factoryTypes = new List<Type>();
        // Find all types that implement IProcessorFactory
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IProcessorFactory).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
            {
                factoryTypes.Add(type);
            }
        }

        foreach (var factoryType in factoryTypes)
        {
            var factoryInstance = (IProcessorFactory?)Activator.CreateInstance(factoryType);
            if (factoryInstance != null)
            {
                Register(factoryInstance);
            }
        }

        return factoryTypes.Count > 0;
    }
}

/// <summary>
/// Retrieves IProcessorFactory instances from the IServiceProvider to create processors.
/// Register the IProcessorFactory implementations in the DI container with a named service 
/// (keyed by namespace) or as a plain IProcessorFactory instance. It is also possible to 
/// register a single IProcessorFactoryProvider that can provide factories on demand.
/// </summary>
public sealed class ProcessorProvider : IProcessorProvider
{
    private readonly IServiceProvider _serviceProvider;

    public IEnumerable<DataTypeDescriptor> DataTypes
        => _serviceProvider.GetServices<IProcessorFactory>().SelectMany(f => f.DataTypes);

    public ProcessorProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider
            ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IProcessor CreateProcessor(ProcessorKey key)
    {
        var factory = GetFactory(key.Namespace)
            ?? throw new ArgumentException($"The Processor factory for '{key.Namespace}' could not be found.");

        var processor = factory.CreateProcessor(key.Id)
            ?? throw new ArgumentException($"The Processor factory for '{key.Namespace} could not create processor '{key.Id}'.");

        return processor;
    }

    public bool TryCreateProcessor(ProcessorKey key, [NotNullWhen(true)] out IProcessor? processor)
    {
        var factory = GetFactory(key.Namespace);
        processor = factory?.CreateProcessor(key.Id);
        return processor is not null;
    }

    private IProcessorFactory? GetFactory(string ns)
    {
        var factory = _serviceProvider.GetKeyedService<IProcessorFactory>(ns);

        if (factory is null)
        {
            var factories = _serviceProvider.GetServices<IProcessorFactory>();
            factory = factories.FirstOrDefault(
                f => f.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase));
        }

        if (factory is null)
        {
            _serviceProvider
                .GetService<IProcessorFactoryProvider>()?
                .TryGetFactory(ns, out factory);
        }

        return factory;
    }
}
