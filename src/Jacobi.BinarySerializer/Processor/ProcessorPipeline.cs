using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Manages the call sequence of processors and the passing of data between them. 
/// The pipeline is divided into stages, each of which can have multiple processors. 
/// The stages are executed in a specific order, and the output of one stage is passed as input to the next stage.
/// </summary>
public sealed class ProcessorPipeline
{
    /// <summary>Root pipeline: unspecified stages are empty (session skips them).</summary>
    public ProcessorPipeline(IEnumerable<ProcessorBinding> processors)
        : this(parent: null, processors) { }

    /// <summary>Child pipeline: stages specified by <paramref name="processors"/> replace the parent's, the rest are reused.</summary>
    public ProcessorPipeline(ProcessorPipeline? parent, IEnumerable<ProcessorBinding> processors)
    {
        List<ProcessorBinding>? value = null, field = null, layout = null, stream = null;

        foreach (var binding in processors)
        {
            var list = binding.Processor.Stage switch
            {
                PipelineStage.Semantic => value ??= [],
                PipelineStage.Representation => field ??= [],
                PipelineStage.Layout => layout ??= [],
                PipelineStage.Stream => stream ??= [],
                _ => throw new InvalidOperationException($"Unknown/invalid pipeline stage: {binding.Processor.Stage}")
            };

            // duplicate processors in the same stage: ignore
            if (!list.Exists(b => b.Processor.Key == binding.Processor.Key))
            {
                list.Add(binding);
            }
        }

        // reuse parent stage instances when not replaced
        ValueProcessors = value ?? parent?.ValueProcessors ?? [];
        FieldProcessors = field ?? parent?.FieldProcessors ?? [];
        LayoutProcessors = layout ?? parent?.LayoutProcessors ?? [];
        StreamProcessors = stream ?? parent?.StreamProcessors ?? [];
    }

    // TODO:
    // write pipeline: value -> field -> layout -> stream
    // read pipeline: stream -> layout -> field -> value

    public IReadOnlyList<ProcessorBinding> ValueProcessors { get; }
    public IReadOnlyList<ProcessorBinding> FieldProcessors { get; }
    public IReadOnlyList<ProcessorBinding> LayoutProcessors { get; }
    public IReadOnlyList<ProcessorBinding> StreamProcessors { get; }

    /// <summary>True when this pipeline only reuses parent stages (no own processors).</summary>
    public static bool IsInheritOnly(IReadOnlyCollection<ProcessorBinding> processors) => processors.Count == 0;
}

public sealed class ProcessorBinding(IProcessor processor, IReadOnlyList<SchemaProperty> properties, string? publishNamespace = null)
{
    public IProcessor Processor { get; } = processor;
    public IReadOnlyList<SchemaProperty> Properties { get; } = properties;

    /// <summary>
    /// The namespace ('pubns') from the schema that overrides the namespace the processor publishes under (null: no override).
    /// </summary>
    public string? PublishNamespace { get; } = publishNamespace;

    /// <summary>
    /// The logger category for this processor (plan-time data; the logger itself is per session).
    /// </summary>
    public string LogCategory { get; } = processor.Key.ToString();

    /// <summary>
    /// The C# type name of the processor, logged in the scope of each entry (plan-time data).
    /// </summary>
    public string ProcessorType { get; } = processor.GetType().Name;
}

public enum PipelineStage
{
    /// <summary>Semantic pipeline stage: logical value transforms (scale, enum mapping, nullability, etc.)</summary>
    Semantic,
    /// <summary>Representation pipeline stage: physical representation transforms (varint, fixed-width, etc.)</summary>
    Representation,
    /// <summary>Layout pipeline stage: bit packing, alignment, endian conversion, etc.</summary>
    Layout,
    /// <summary>Stream pipeline stage: framing, compression, encryption, etc.</summary>
    Stream
}
