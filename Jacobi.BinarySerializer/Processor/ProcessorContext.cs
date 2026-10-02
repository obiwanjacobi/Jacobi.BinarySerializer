using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processor;

public closed class ProcessorContext
{
    // is on field/group object
    //public SchemaObject SchemaObject { get; init; }

    // the current pipeline stage for this context, which determines what processors are available
    public PipelineStage Stage { get; init; }

    /// <summary>
    /// Collected properties for the processor, including properties from the schema and any additional properties (TBD).
    /// </summary>
    public required IReadOnlyList<SchemaProperty> ProcessorProperties { get; init; }
    public required IServiceProvider Services { get; init; }

    // allow processors to store arbitrary state in the context
    // - they cannot read each other's state

    // publish dynamic values for processors to use, e.g. a data-length value read from the message header.
    // consume dynamic values published by other processors.

    // diagnostics:
    // - log messages
}

public sealed class ValueProcessorContext : ProcessorContext
{
    // field info
}

public sealed class FieldProcessorContext : ProcessorContext
{
    // field info
}

public sealed class LayoutProcessorContext : ProcessorContext
{
    // group info
}

public sealed class StreamProcessorContext : ProcessorContext
{
    // group info
}
