using System.Runtime.InteropServices;

namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Maintains the state of a processing session, including the pipeline and context.
/// </summary>
public closed class SessionState
{
    // private processor state
    private readonly Dictionary<ProcessorBinding, object> _private = new(ReferenceEqualityComparer.Instance);

    internal T GetOrCreate<T>(ProcessorBinding owner) where T : class, new()
        => (T)(CollectionsMarshal.GetValueRefOrAddDefault(_private, owner, out _) ??= new T());

    // shared processor state
    // buffer state/management
}

public sealed class WriterSession : SessionState
{
}

public sealed class ReaderSession : SessionState
{
}
