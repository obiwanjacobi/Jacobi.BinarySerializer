using System.Diagnostics.CodeAnalysis;

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

        if (_documents.Any(doc => !doc.Value.IsCompiled))
            throw new ArgumentException("The documents passed to the SchemaSet must all be compiled (IsCompiled=True).");
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

        return match;
    }

    /// <summary>
    /// Finds the document that holds <paramref name="node"/> (the instance itself, at any depth in a root or node def);
    /// null when the node is not part of this set.
    /// </summary>
    public SchemaDocument? FindDocument(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _documents.Values.FirstOrDefault(document => ContainsNode(document.Roots, node) || ContainsNode(document.NodeDefs, node));
    }

    private static bool ContainsNode(IEnumerable<SchemaNode> nodes, SchemaNode target)
        => nodes.Any(node => ReferenceEquals(node, target) || (node is SchemaGroup group && ContainsNode(group.Members, target)));

    /// <summary>
    /// Finds a node by its schema name.
    /// The namespace of the name identifies the document and the name identifies the node inside that document.
    /// The node can be nested at any depth; node names are assumed to be unique within a document.
    /// </summary>
    public SchemaNode FindByName(SchemaName name)
    {
        if (!TryFindByName(name, out var node))
        {
            throw new InvalidOperationException($"Schema node '{name}' not found.");
        }
        return node;
    }

    /// <summary>
    /// Finds a node by its schema name (namespace = document, name = node inside that document).
    /// </summary>
    public T FindByName<T>(SchemaName name)
        where T : SchemaNode
    {
        var node = FindByName(name);
        return node as T
            ?? throw new InvalidOperationException($"Schema node '{name}' is a {node.GetType().Name}, not a {typeof(T).Name}.");
    }

    /// <summary>
    /// Tries to find a node by its schema name.
    /// The namespace of the name identifies the document and the name identifies the node (at any depth) inside that document.
    /// </summary>
    public bool TryFindByName(SchemaName name, [NotNullWhen(true)] out SchemaNode? node)
    {
        node = null;
        if (!_documents.TryGetValue(name.Namespace, out var document))
            return false;

        node = FindByNameIn(document.Roots, name.Name);
        return node is not null;
    }

    private static SchemaNode? FindByNameIn(IEnumerable<SchemaNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (String.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                return node;

            if (node is SchemaGroup group)
            {
                var found = FindByNameIn(group.Members, name);
                if (found is not null)
                    return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds any node by its path: 'document.root.member.member...'.
    /// The first segment(s) identify the document, the remaining segments the node names from the root down.
    /// </summary>
    public SchemaNode FindByPath(SchemaPath path)
    {
        if (!TryFindByPath(path, out var node))
        {
            throw new InvalidOperationException($"Schema node '{path}' not found.");
        }
        return node;
    }

    public T FindByPath<T>(SchemaPath path)
        where T : SchemaNode
    {
        var node = FindByPath(path);
        return node as T
            ?? throw new InvalidOperationException($"Schema node '{path}' is a {node.GetType().Name}, not a {typeof(T).Name}.");
    }

    public bool TryFindByPath(SchemaPath path, [NotNullWhen(true)] out SchemaNode? node)
    {
        node = null;
        var segments = path.Segments.ToArray();

        // document names may contain separators: try the shortest document prefix first
        for (int docLength = 1; docLength < segments.Length; docLength++)
        {
            var documentName = String.Join(SchemaPath.Separator, segments[..docLength]);
            if (!_documents.TryGetValue(documentName, out var document))
                continue;

            var current = FindMember(document.Roots, segments[docLength]);
            for (int i = docLength + 1; current is not null && i < segments.Length; i++)
            {
                current = current is SchemaGroup group ? FindMember(group.Members, segments[i]) : null;
            }

            if (current is not null)
            {
                node = current;
                return true;
            }
        }

        return false;
    }

    private static SchemaNode? FindMember(IEnumerable<SchemaNode> nodes, string name)
        => nodes.FirstOrDefault(n => String.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds all nodes (at any depth, in all documents) that have all the specified properties (AND).
    /// Property names are matched case-insensitively; values are matched ordinally.
    /// </summary>
    public IReadOnlyList<SchemaNode> FindByProperties(IEnumerable<KeyValuePair<string, string?>> properties)
    {
        var criteria = properties.ToArray();
        var result = new List<SchemaNode>();
        foreach (var document in _documents.Values)
            CollectByProperties(document.Roots, criteria, result);
        return result;
    }

    public IReadOnlyList<SchemaNode> FindByProperty(string propertyName, string? value)
        => FindByProperties([new KeyValuePair<string, string?>(propertyName, value)]);

    public IReadOnlyList<T> FindByProperties<T>(IEnumerable<KeyValuePair<string, string?>> properties)
        where T : SchemaNode
        => FindByProperties(properties).OfType<T>().ToList();

    public IReadOnlyList<T> FindByProperty<T>(string propertyName, string? value)
        where T : SchemaNode
        => FindByProperties<T>([new KeyValuePair<string, string?>(propertyName, value)]);

    private static void CollectByProperties(
        IEnumerable<SchemaNode> nodes, KeyValuePair<string, string?>[] criteria, List<SchemaNode> result)
    {
        foreach (var node in nodes)
        {
            if (criteria.All(c => node.Properties.Any(p =>
                    String.Equals(p.Name, c.Key, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(p.Value, c.Value, StringComparison.Ordinal))))
            {
                result.Add(node);
            }

            if (node is SchemaGroup group)
                CollectByProperties(group.Members, criteria, result);
        }
    }
}
