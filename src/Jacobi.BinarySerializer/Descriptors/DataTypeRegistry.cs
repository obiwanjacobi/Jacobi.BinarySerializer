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

        var baseDescriptor = ResolveReference(document, def.BasedOn, all, visiting)
            ?? throw new InvalidOperationException($"The data type '{def.BasedOn}' that '{name}' is based on is not registered.");

        // the value type: explicit, or implied by the facets of this def (options: string, scale/shift: decimal), or inherited.
        var valueTypeName = def.ValueType
            ?? (def.Options is not null ? SystemName("string")
            : def.Scale is not null || def.Shift is not null ? SystemName("decimal")
            : (SchemaName?)null);
        DataTypeDescriptor? valueType = null;
        if (valueTypeName is { } valueTypeReference)
        {
            valueType = ResolveReference(document, valueTypeReference, all, visiting)?.ValueType
                ?? throw new InvalidOperationException($"The value type '{valueTypeReference}' of '{name}' is not registered.");
        }

        var descriptor = baseDescriptor.Derive(name, def, valueType);
        _types[name.FullName] = descriptor;
        visiting.Remove(def);
        return descriptor;
    }

    // a data type def in the documents (without a namespace: in the referring document) or a registered type.
    private DataTypeDescriptor? ResolveReference(SchemaDocument document, SchemaName reference, List<(SchemaDocument Document, SchemaDataTypeDef Def)> all, HashSet<SchemaDataTypeDef> visiting)
    {
        var owner = String.IsNullOrEmpty(reference.Namespace) ? document.Name : reference.Namespace;
        var found = all
            .Where(a => String.Equals(a.Document.Name, owner, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.Def.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            .Select(a => ((SchemaDocument, SchemaDataTypeDef)?)(a.Document, a.Def))
            .FirstOrDefault();

        if (found is var (foundDocument, foundDef))
        {
            return Resolve(foundDocument, foundDef, all, visiting);
        }
        if (!String.IsNullOrEmpty(reference.Namespace) && TryGet(reference, out var descriptor))
        {
            return descriptor;
        }
        return null;
    }

    private static SchemaName SystemName(string name) => new($"{SystemNamespace}{SchemaName.Separator}{name}");

    public bool TryGet(SchemaName name, [NotNullWhen(true)] out DataTypeDescriptor? descriptor)
        => _types.TryGetValue(name.FullName, out descriptor);

    /// <summary>Gets a descriptor or throws when the name is not registered.</summary>
    public DataTypeDescriptor Get(SchemaName name)
        => TryGet(name, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"The data type '{name}' is not registered.");

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
