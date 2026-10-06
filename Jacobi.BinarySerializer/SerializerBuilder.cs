using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer;

/// <summary>
/// Mutable configuration stage for a <see cref="Serializer"/>.
/// Once <see cref="Build"/> is called the builder can no longer be modified.
/// </summary>
public sealed class SerializerBuilder
{
    private SchemaSet? _schemas;
    private IProcessorProvider? _processorProvider;
    private IServiceProvider? _serviceProvider;
    private bool _built;

    public SerializerBuilder AddSchemas(SchemaSet schemas)
    {
        ThrowIfBuilt();
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        return this;
    }

    public SerializerBuilder AddProcessors(IProcessorProvider processorProvider)
    {
        ThrowIfBuilt();
        _processorProvider = processorProvider ?? throw new ArgumentNullException(nameof(processorProvider));
        return this;
    }

    public SerializerBuilder AddServices(IServiceProvider serviceProvider)
    {
        ThrowIfBuilt();
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        return this;
    }

    public Serializer Build()
    {
        ThrowIfBuilt();
        if (_schemas is null)
            throw new InvalidOperationException("No schemas were added. Call AddSchemas first.");

        var processorProvider = _processorProvider
            ?? (_serviceProvider is not null ? new ProcessorProvider(_serviceProvider) : null)
            ?? throw new InvalidOperationException("No processors were added. Call AddProcessors or AddServices first.");

        _built = true;
        return new Serializer(_schemas, processorProvider, _serviceProvider);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("The serializer was already built; the builder cannot be modified.");
    }
}
