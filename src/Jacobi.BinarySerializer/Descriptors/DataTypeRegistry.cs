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

    /// <summary>
    /// Registers a derived descriptor ('DocumentName.Name') for each data type definition in the documents.
    /// Definitions that are already registered are skipped.
    /// </summary>
    public void RegisterDataTypeDefs(IEnumerable<SchemaDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var all = documents.SelectMany(d => d.DataTypeDefs.Select(def => (Document: d, Def: def))).ToList();

        foreach (var (document, def) in all)
        {
            Resolve(document, def, all, []);
        }
    }

    private DataTypeDescriptor Resolve(SchemaDocument document, SchemaDataTypeDef def, List<(SchemaDocument Document, SchemaDataTypeDef Def)> all, HashSet<SchemaDataTypeDef> visiting)
    {
        var name = new SchemaName($"{document.Name}{SchemaName.Separator}{def.Name}");
        if (_types.TryGetValue(name.FullName, out var existing))
        {
            return existing;
        }
        if (!visiting.Add(def))
        {
            throw new InvalidOperationException($"Circular data type definition '{name}'.");
        }

        var baseName = def.BasedOn.Name;
        var local = all.Where(a => ReferenceEquals(a.Document, document) &&
                String.Equals(a.Def.Name, baseName.Name, StringComparison.OrdinalIgnoreCase) &&
                !ReferenceEquals(a.Def, def) &&
                String.Equals(baseName.Namespace, SystemNamespace, StringComparison.OrdinalIgnoreCase))
            .Select(a => ((SchemaDocument, SchemaDataTypeDef)?)(a.Document, a.Def))
            .FirstOrDefault();
        var qualified = all.Where(a => String.Equals(a.Document.Name, baseName.Namespace, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.Def.Name, baseName.Name, StringComparison.OrdinalIgnoreCase))
            .Select(a => ((SchemaDocument, SchemaDataTypeDef)?)(a.Document, a.Def))
            .FirstOrDefault();

        DataTypeDescriptor baseDescriptor;
        if ((local ?? qualified) is var (baseDocument, baseDef))
        {
            baseDescriptor = Resolve(baseDocument, baseDef, all, visiting);
        }
        else if (!TryGet(baseName, out baseDescriptor!))
        {
            throw new InvalidOperationException($"The data type '{baseName}' that '{name}' is based on is not registered.");
        }

        var descriptor = baseDescriptor.Derive(name, def);
        _types[name.FullName] = descriptor;
        visiting.Remove(def);
        return descriptor;
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
