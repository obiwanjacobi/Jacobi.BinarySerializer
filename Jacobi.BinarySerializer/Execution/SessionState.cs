using System.Runtime.InteropServices;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Maintains the state of a processing session, including the pipeline and context.
/// </summary>
public closed class SessionState
{
    // private processor state
    private readonly Dictionary<ProcessorBinding, object> _private = new(ReferenceEqualityComparer.Instance);

    // TODO: needs additional key-data to differentiate between processors of the same type, so we use ProcessorBinding as the key
    internal T GetOrCreate<T>(ProcessorBinding owner) where T : class, new()
        => (T)(CollectionsMarshal.GetValueRefOrAddDefault(_private, owner, out _) ??= new T());

    // shared processor state
    // buffer state/management
}

