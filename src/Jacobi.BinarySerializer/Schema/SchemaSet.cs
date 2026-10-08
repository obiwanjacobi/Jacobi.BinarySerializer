using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Jacobi.BinarySerializer.Schema.Json;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Schema;

public sealed class SchemaSet
{
    private readonly Dictionary<string, SchemaDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SchemaDocument> Documents
        => _documents.Values;

    public SchemaDocument LoadFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        var document = extension switch
        {
            ".xml" => LoadFromXml(content),
            ".json" => LoadFromJson(content),
            ".yaml" or ".yml" => throw new NotSupportedException("YAML requires the Jacobi.BinarySerializer.Yaml package: use SchemaSet.LoadYaml()."),
            _ => throw new NotSupportedException($"File extension '{extension}' is not supported.")
        };

        AddDocument(document);
        return document;
    }

    public IEnumerable<SchemaDocument> LoadAssembly(string path, string resourcePath)
        => LoadAssembly(Assembly.LoadFile(path), resourcePath);

    public IEnumerable<SchemaDocument> LoadAssembly(Assembly assembly, string resourcePath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var documents = new List<SchemaDocument>();

        assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePath, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .ForEach(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Resource '{name}' not found in assembly '{assembly.FullName}'.");
                var document = LoadFromBinary(stream);

                documents.Add(document);
                AddDocument(document);
            });

        return documents;
    }

    public SchemaDocument LoadFromXml(string xml)
        => XmlSerializer.Deserialize(xml);

    public SchemaDocument LoadFromJson(string json)
        => JsonSerializer.Deserialize(json);

    public SchemaDocument LoadFromBinary(Stream data)
    {
        throw new NotImplementedException();
    }

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

    public void Compile()
    {
        foreach (var document in _documents.Values)
        {
            document.IsCompiled = false;
        }

        var dependenciesByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var dependentsByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolvedDependencies = new List<string>();

        foreach (var document in _documents.Values)
        {
            var dependencies = GetDocumentDependencies(document);
            dependenciesByDocument[document.Name] = dependencies;

            foreach (var dependency in dependencies)
            {
                if (!_documents.ContainsKey(dependency))
                {
                    unresolvedDependencies.Add($"{document.Name} -> {dependency}");
                    continue;
                }

                if (!dependentsByDocument.TryGetValue(dependency, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dependentsByDocument[dependency] = dependents;
                }

                dependents.Add(document.Name);
            }
        }

        if (unresolvedDependencies.Count > 0)
        {
            throw new InvalidOperationException(
                $"Missing schema dependencies: {String.Join(", ", unresolvedDependencies)}");
        }

        var queue = new Queue<string>(dependenciesByDocument
            .Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        int compiledCount = 0;
        while (queue.Count > 0)
        {
            var documentName = queue.Dequeue();
            var document = _documents[documentName];

            ExpandPropertyNames(document);

            if (!ResolveReferences(document))
            {
                throw new InvalidOperationException(
                    $"Failed to resolve references in schema '{document.Name}'.");
            }

            document.IsCompiled = true;
            compiledCount++;

            if (!dependentsByDocument.TryGetValue(documentName, out var dependents))
            {
                continue;
            }

            foreach (var dependent in dependents.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var dependencies = dependenciesByDocument[dependent];
                dependencies.Remove(documentName);
                if (dependencies.Count == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (compiledCount != _documents.Count)
        {
            var cyclicDocuments = dependenciesByDocument
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => pair.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            throw new InvalidOperationException(
                $"Circular schema dependencies detected: {String.Join(", ", cyclicDocuments)}");
        }
    }

    /// <summary>
    /// Expands short property names on processor refs to their full 'namespace.id.name' form.
    /// Names that already contain '.' are left as is. Properties of a 'ref:' are expanded when it is resolved.
    /// Properties on fields and groups are not touched.
    /// </summary>
    private static void ExpandPropertyNames(SchemaDocument document)
    {
        foreach (var typeDef in document.TypeDefs)
        {
            foreach (var processor in typeDef.Processors)
            {
                ExpandPropertyNames(processor);
            }
        }

        foreach (var processor in document.ProcessorDefs)
        {
            ExpandPropertyNames(processor);
        }

        foreach (var root in document.Roots)
        {
            ExpandPropertyNames(root);
        }
    }

    private static void ExpandPropertyNames(SchemaNode node)
    {
        var processors = node switch
        {
            SchemaField field => field.Processors,
            SchemaGroup group => group.Processors,
            _ => []
        };

        foreach (var processor in processors.Concat(ValueProcessorsOf(node)))
        {
            ExpandPropertyNames(processor);
        }

        if (node is SchemaGroup g)
        {
            foreach (var child in g.MemberList)
            {
                ExpandPropertyNames(child);
            }
        }
    }

    private static void ExpandPropertyNames(SchemaProcessorDef processor)
        => ExpandPropertyNames(processor, processor.Processor);

    private static void ExpandPropertyNames(SchemaProcessorRef processor)
    {
        if (processor.Key is { } key)
        {
            ExpandPropertyNames(processor, key);
        }
    }

    private static void ExpandPropertyNames(SchemaProcessor processor, Processor.ProcessorKey key)
    {
        var list = processor.PropertyList;
        for (var i = 0; i < list.Count; i++)
        {
            var property = list[i];
            if (property.Name.Contains(Processor.ProcessorKey.Separator))
            {
                continue;
            }

            list[i] = new SchemaProperty
            {
                Name = key.PropertyName(property.Name),
                Value = property.Value,
            };
        }
    }

    private static HashSet<string> GetDocumentDependencies(SchemaDocument document)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var include in document.Includes)
        {
            if (!String.Equals(include.Schema, document.Name, StringComparison.OrdinalIgnoreCase))
            {
                dependencies.Add(include.Schema);
            }
        }

        foreach (var typeDef in document.TypeDefs)
        {
            AddSchemaDependency(typeDef.TypeDef, document.Name, dependencies);
            foreach (var processor in typeDef.Processors)
            {
                AddSchemaDependency(processor, document.Name, dependencies);
            }
        }

        foreach (var root in document.Roots)
        {
            CollectNodeDependencies(root.MemberList, document.Name, dependencies);
        }

        return dependencies;
    }

    private static void CollectNodeDependencies(IReadOnlyList<SchemaNode> nodes, string documentName, HashSet<string> dependencies)
    {
        foreach (var node in nodes)
        {
            AddSchemaDependency(node.TypeDef, documentName, dependencies);

            if (node is SchemaField field)
            {
                foreach (var processor in field.Processors)
                {
                    AddSchemaDependency(processor, documentName, dependencies);
                }
            }

            if (node is SchemaGroup group)
            {
                foreach (var processor in group.Processors.Concat(ValueProcessorsOf(group)))
                {
                    AddSchemaDependency(processor, documentName, dependencies);
                }

                CollectNodeDependencies(group.MemberList, documentName, dependencies);
            }
        }
    }

    private static void AddSchemaDependency(SchemaProcessorRef processorRef, string documentName, HashSet<string> dependencies)
    {
        if (processorRef.Processor.IsReference)
        {
            AddSchemaDependency(processorRef.Processor.ToSchemaName(), documentName, dependencies);
        }
    }

    private static void AddSchemaDependency(SchemaName? schemaName, string documentName, HashSet<string> dependencies)
    {
        if (!schemaName.HasValue || String.IsNullOrEmpty(schemaName.Value.Namespace))
        {
            return;
        }

        var dependency = schemaName.Value.Namespace!;
        if (!String.Equals(dependency, documentName, StringComparison.OrdinalIgnoreCase))
        {
            dependencies.Add(dependency);
        }
    }

    internal void AddDocument(SchemaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (String.IsNullOrEmpty(document.Name))
        {
            throw new ArgumentException("Document name cannot be null or empty.", nameof(document));
        }
        if (_documents.ContainsKey(document.Name))
        {
            throw new InvalidOperationException($"A document with the name '{document.Name}' already exists.");
        }

        _documents[document.Name] = document;
    }

    private bool ResolveReferences(SchemaDocument document)
    {
        bool allResolved = ResolveIncludes(document);

        foreach (var typeDef in document.TypeDefs)
        {
            foreach (var processor in typeDef.Processors)
            {
                if (!TryResolveProcessorRef(document, processor))
                {
                    allResolved = false;
                }
            }
        }

        foreach (var group in document.Roots)
        {
            foreach (var processor in group.Processors)
            {
                if (!TryResolveProcessorRef(document, processor))
                {
                    allResolved = false;
                }
            }

            if (!ResolveReferences(document, group.MemberList))
            {
                allResolved = false;
            }
        }

        return allResolved;
    }

    private bool ResolveIncludes(SchemaDocument document)
    {
        bool allResolved = true;

        foreach (var reference in document.Includes)
        {
            if (_documents.TryGetValue(reference.Schema, out var includedDocument))
            {
                reference.SchemaDocument = includedDocument;
            }
            else
            {
                allResolved = false;
            }
        }

        return allResolved;
    }

    private bool ResolveReferences(SchemaDocument document, List<SchemaNode> schemaNodes)
    {
        bool allResolved = true;
        var resolvedNodes = new List<SchemaNode>(schemaNodes);

        // Resolve references for groups, fields and processors
        foreach (var node in schemaNodes)
        {
            if (node.TypeDef.HasValue)
            {
                if (document.TryFindTypeDef(node.TypeDef.Value, out var typeDef) ||
                    TryFindTypeDef(node.TypeDef.Value, out typeDef))
                {
                    var replacementNode = InstantiateType(node, typeDef);
                    resolvedNodes.Replace(node, replacementNode);
                }
                else
                {
                    allResolved = false;
                }
            }

            if (node is SchemaField field)
            {
                foreach (var processor in field.Processors)
                {
                    if (!TryResolveProcessorRef(document, processor))
                    {
                        allResolved = false;
                    }
                }
            }

            if (node is SchemaGroup group)
            {
                foreach (var processor in group.Processors.Concat(ValueProcessorsOf(group)))
                {
                    if (!TryResolveProcessorRef(document, processor))
                    {
                        allResolved = false;
                    }
                }

                if (!ResolveReferences(document, group.MemberList))
                {
                    allResolved = false;
                }
            }
        }

        schemaNodes.Clear();
        schemaNodes.AddRange(resolvedNodes);

        return allResolved;
    }

    private static IEnumerable<SchemaProcessorRef> ValueProcessorsOf(SchemaNode node)
        => node switch
        {
            SchemaRepeat repeat => repeat.ValueProcessors,
            SchemaChoice choice => choice.ValueProcessors,
            _ => []
        };

    private SchemaNode InstantiateType(SchemaNode node, SchemaTypeDef typeDef)
    {
        if (node is SchemaField fieldNode)
        {
            return new SchemaField
            {
                Name = node.Name,
                DataType = typeDef.DataType ?? throw new InvalidOperationException($"The typedef '{typeDef.Name}' has no data type and cannot be applied to the field '{node.Name}'."),
                Value = fieldNode.Value,
                Length = fieldNode.Length,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, fieldNode.Processors),
            };
        }
        if (node is SchemaRepeat repeatNode)
        {
            return new SchemaRepeat
            {
                Name = node.Name,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, repeatNode.Processors),
                MemberList = [.. repeatNode.MemberList],
                Count = repeatNode.Count,
                Size = repeatNode.Size,
                ValueProcessorsList = [.. repeatNode.ValueProcessors],
            };
        }
        if (node is SchemaChoice choiceNode)
        {
            return new SchemaChoice
            {
                Name = node.Name,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, choiceNode.Processors),
                MemberList = [.. choiceNode.MemberList],
                SelectedIndex = choiceNode.SelectedIndex,
                Size = choiceNode.Size,
                ValueProcessorsList = [.. choiceNode.ValueProcessors],
            };
        }
        if (node is SchemaGroup groupNode)
        {
            return new SchemaGroup
            {
                Name = node.Name,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, groupNode.Processors),
                MemberList = [.. groupNode.MemberList],
                Size = groupNode.Size,
            };
        }

        throw new NotSupportedException($"Type '{typeDef.Kind}' is not supported.");
    }

    private List<SchemaProperty> MergeProperties(List<SchemaProperty> properties, IReadOnlyList<SchemaProperty> overrides)
    {
        var mergedProperties = new List<SchemaProperty>(properties);
        foreach (var overrideProp in overrides)
        {
            var existingProp = mergedProperties.FirstOrDefault(p => p.Name == overrideProp.Name);
            if (existingProp != null)
            {
                mergedProperties.Remove(existingProp);
            }
            mergedProperties.Add(overrideProp);
        }
        return mergedProperties;
    }

    private List<SchemaProcessorRef> MergeProcessors(IReadOnlyList<SchemaProcessorRef> processors, IReadOnlyList<SchemaProcessorRef> overrides)
    {
        var mergedProcessors = new List<SchemaProcessorRef>(processors);
        foreach (var overrideProcessor in overrides)
        {
            var existingProcessor = mergedProcessors.FirstOrDefault(c => c.Processor == overrideProcessor.Processor);
            if (existingProcessor != null)
            {
                mergedProcessors.Remove(existingProcessor);
            }
            mergedProcessors.Add(overrideProcessor);
        }
        return mergedProcessors;
    }

    /// <summary>
    /// Resolves a 'ref:name' (this document) or 'ref:document.name' (any other document) to its definition.
    /// A processor key ('namespace.id') needs no resolving.
    /// </summary>
    private bool TryResolveProcessorRef(SchemaDocument document, SchemaProcessorRef processorRef)
    {
        if (!processorRef.Processor.IsReference)
        {
            return true;
        }

        var ns = processorRef.Processor.Namespace;
        var definingDocument = String.IsNullOrEmpty(ns) || String.Equals(ns, document.Name, StringComparison.OrdinalIgnoreCase)
            ? document
            : _documents.GetValueOrDefault(ns);

        if (definingDocument is null ||
            !definingDocument.TryFindProcessorDef(processorRef.Processor.Name, out var definition))
        {
            return false;
        }

        processorRef.Definition = definition;
        ExpandPropertyNames(processorRef, definition.Processor);
        return true;
    }

    private bool TryFindTypeDef(SchemaName schemaName, [NotNullWhen(true)] out SchemaTypeDef? typeDef)
    {
        if (!String.IsNullOrEmpty(schemaName.Namespace) &&
            _documents.TryGetValue(schemaName.Namespace, out var document))
        {
            return document.TryFindTypeDef(schemaName, out typeDef);
        }

        typeDef = null;
        return false;
    }
}

internal static class SchemaDocumentExtensions
{
    public static bool TryFindTypeDef(this SchemaDocument document, SchemaName schemaName, [NotNullWhen(true)] out SchemaTypeDef? node)
    {
        if (String.IsNullOrEmpty(schemaName.Namespace))
        {
            node = document.TypeDefs.FirstOrDefault(n => n.Name == schemaName.Name);
            return node != null;
        }

        node = null;
        return false;
    }

    public static bool TryFindProcessorDef(this SchemaDocument document, string name, [NotNullWhen(true)] out SchemaProcessorDef? processorDef)
    {
        processorDef = document.ProcessorDefs.FirstOrDefault(n => n.Name == name);
        return processorDef != null;
    }
}

internal static class ListExtensions
{
    public static bool Replace<T>(this List<T> list, T oldItem, T newItem)
    {
        if (oldItem is null || newItem is null)
        {
            throw new ArgumentNullException(oldItem is null ? nameof(oldItem) : nameof(newItem));
        }

        var index = list.IndexOf(oldItem);
        if (index != -1)
        {
            list[index] = newItem;
            return true;
        }
        return false;
    }
}
