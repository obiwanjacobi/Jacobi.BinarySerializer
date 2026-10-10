using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Descriptors;

/// <summary>
/// Read-only view on the registered <see cref="DataTypeDescriptor"/>s.
/// Exposed to processors: data types cannot be changed during processing.
/// </summary>
public interface IDataTypeRegistry
{
    IEnumerable<DataTypeDescriptor> Types { get; }

    bool TryGet(SchemaName name, [NotNullWhen(true)] out DataTypeDescriptor? descriptor);

    /// <summary>Gets a descriptor or throws when the name is not registered.</summary>
    DataTypeDescriptor Get(SchemaName name);
}
