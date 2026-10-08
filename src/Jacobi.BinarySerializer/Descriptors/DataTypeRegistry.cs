using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Descriptors;

/// <summary>
/// A collection of <see cref="DataTypeDescriptor"/>s by name.
/// Create one with <see cref="CreateDefault"/> to get the built-in types, then register custom types.
/// </summary>
public sealed class DataTypeRegistry : IDataTypeRegistry
{
    /// <summary>The namespace of the built-in data types.</summary>
    public const string SystemNamespace = "sys";

    private readonly Dictionary<string, DataTypeDescriptor> _types = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<DataTypeDescriptor> Types => _types.Values;

    /// <summary>Registers a descriptor. Throws when the name is already registered.</summary>
    public void Register(DataTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_types.TryAdd(descriptor.Name.FullName, descriptor))
        {
            throw new InvalidOperationException($"The data type '{descriptor.Name}' is already registered.");
        }
    }

    public bool TryGet(SchemaName name, [NotNullWhen(true)] out DataTypeDescriptor? descriptor)
        => _types.TryGetValue(name.FullName, out descriptor);

    /// <summary>Gets a descriptor or throws when the name is not registered.</summary>
    public DataTypeDescriptor Get(SchemaName name)
        => TryGet(name, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"The data type '{name}' is not registered.");

    /// <summary>Gets the descriptor of a data type or throws when it is not registered.</summary>
    public DataTypeDescriptor Get(SchemaDataType type)
        => TryGet(type, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"The data type '{type}' is not registered.");

    public bool TryGet(SchemaDataType type, [NotNullWhen(true)] out DataTypeDescriptor? descriptor)
    {
        return TryGet(type.Name, out descriptor);
    }

    /// <summary>Creates a registry with the built-in data types.</summary>
    /// <summary>Creates a registry prepopulated with the built-in data types.</summary>
    public static DataTypeRegistry CreateDefault()
    {
        var registry = new DataTypeRegistry();
        foreach (var descriptor in BuiltInDataTypes.Create())
        {
            registry.Register(descriptor);
        }
        return registry;
    }
}
