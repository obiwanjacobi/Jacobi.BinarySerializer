using System.Diagnostics.CodeAnalysis;

namespace Jacobi.BinarySerializer.Processor;

public interface IProcessorFactory
{
    string Namespace { get; }

    IProcessor? CreateProcessor(string id);
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
}

public sealed class ProcessorManager : IProcessorProvider, IProcessorFactoryProvider
{
    private readonly Dictionary<string, IProcessorFactory> _processorFactories = new(StringComparer.OrdinalIgnoreCase);

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
            return processor != null;
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
    {
        var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);

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
