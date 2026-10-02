namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Maintains the state of a processing session, including the pipeline and context.
/// </summary>
public closed class ProcessorSession
{
    public required ProcessorPipeline Pipeline { get; init; }

    // this should probably be top object that refs this session /most specific.
    public required ProcessorContext Context { get; init; }

    // private processor state
    // shared processor state
    // buffer state/management
}

public sealed class WriterSession : ProcessorSession
{
}

public sealed class ReaderSession : ProcessorSession
{
}
