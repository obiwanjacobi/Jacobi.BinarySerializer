namespace Jacobi.BinarySerializer.Processor;

public sealed class ProcessorPipeline
{
    public ProcessorPipeline(IEnumerable<IProcessor> processors)
    {
        // TODO: detect duplicate processors in the same stage (ignore)

        foreach (var processor in processors)
        {
            switch (processor.Stage)
            {
                case PipelineStage.Semantic:
                    _valueProcessors.Add((IValueProcessor)processor);
                    break;
                case PipelineStage.Representation:
                    _fieldProcessors.Add((IFieldProcessor)processor);
                    break;
                case PipelineStage.Layout:
                    _layoutProcessors.Add((ILayoutProcessor)processor);
                    break;
                case PipelineStage.Stream:
                    _streamProcessors.Add((IStreamProcessor)processor);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown/invalid pipeline stage: {processor.Stage}");
            }
        }

        if (_valueProcessors.Count == 0)
        {
            _valueProcessors.Add(new NullValueProcessor());
        }
        if (_fieldProcessors.Count == 0)
        {
            _fieldProcessors.Add(new NullFieldProcessor());
        }
        if (_layoutProcessors.Count == 0)
        {
            _layoutProcessors.Add(new NullLayoutProcessor());
        }
        if (_streamProcessors.Count == 0)
        {
            _streamProcessors.Add(new NullStreamProcessor());
        }
    }

    // TODO:
    // write pipeline: value -> field -> layout -> stream
    // read pipeline: stream -> layout -> field -> value

    // replace with the supplied processors in pipeline stages, leave the rest intact
    // ProcessorPipeline ReplaceWith(IEnumerable<IProcessor> processors)

    private readonly List<IValueProcessor> _valueProcessors = new();
    public IReadOnlyList<IValueProcessor> ValueProcessors => _valueProcessors;

    private readonly List<IFieldProcessor> _fieldProcessors = new();
    public IReadOnlyList<IFieldProcessor> FieldProcessors => _fieldProcessors;

    private readonly List<ILayoutProcessor> _layoutProcessors = new();
    public IReadOnlyList<ILayoutProcessor> LayoutProcessors => _layoutProcessors;

    private readonly List<IStreamProcessor> _streamProcessors = new();
    public IReadOnlyList<IStreamProcessor> StreamProcessors => _streamProcessors;
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
