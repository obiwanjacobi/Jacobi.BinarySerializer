namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// A read-only set of compiled schema documents. Create one using <see cref="SchemaSetBuilder.Compile"/>.
/// </summary>
public sealed class SchemaSet
{
    private readonly Dictionary<string, SchemaDocument> _documents;

    internal SchemaSet(IReadOnlyDictionary<string, SchemaDocument> documents)
    {
        _documents = new Dictionary<string, SchemaDocument>(documents, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SchemaDocument> Documents
        => _documents.Values;

    public SchemaGroup FindRoot(SchemaName root)
    {
        if (!_documents.TryGetValue(root.Namespace, out var document))
        {
            throw new InvalidOperationException($"Schema root '{root}' not found.");
        }

        var match = document.Roots
            .Where(r => String.Equals(r.Name, root.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (match == null)
            throw new InvalidOperationException($"Schema root '{root}' not found in document '{document.Name}'.");

        if (!document.IsCompiled)
            throw new InvalidOperationException($"Schema document '{document.Name}' is not compiled.");

        return match;
    }
}
