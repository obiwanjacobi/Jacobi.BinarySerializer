using Jacobi.BinarySerializer.Descriptors;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Base class for the built-in processors.
/// Lets a processor publish the data types its properties use, so the factory can collect them.
/// </summary>
internal abstract class ProcessorBase
{
    public virtual IEnumerable<DataTypeDescriptor> DataTypes => [];
}
