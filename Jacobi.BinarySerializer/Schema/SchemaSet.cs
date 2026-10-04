using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Jacobi.BinarySerializer.Schema.Json;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Schema;

public sealed class SchemaSet
{
    private readonly Dictionary<string, SchemaDocument> _documents = new();

    public IReadOnlyCollection<SchemaDocument> Documents
        => _documents.Values;

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
    /// Expands short property names on processor refs to their full 'namespace:id.name' form.
    /// Names that already contain ':' and refs without a namespace (processor-def aliases) are left as is.
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

        foreach (var processor in processors)
        {
            ExpandPropertyNames(processor);
        }

        if (node is SchemaGroup g)
        {
            foreach (var child in g.ChildList)
            {
                ExpandPropertyNames(child);
            }
        }
    }

    private static void ExpandPropertyNames(SchemaProcessorRef processor)
    {
        var ns = processor.Processor.Namespace;
        if (String.IsNullOrEmpty(ns))
        {
            return;
        }

        var key = new Processor.ProcessorKey(ns, processor.Processor.Name);
        var list = processor.PropertyList;
        for (var i = 0; i < list.Count; i++)
        {
            var property = list[i];
            if (property.Name.Contains(':'))
            {
                continue;
            }

            list[i] = new SchemaProperty
            {
                Name = key.PropertyName(property.Name),
                Value = property.Value,
                DataType = property.DataType
            };
        }
    }

    private HashSet<string> GetDocumentDependencies(SchemaDocument document)
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
                AddSchemaDependency(processor.Processor, document.Name, dependencies);
            }
        }

        foreach (var processor in document.ProcessorDefs)
        {
            AddSchemaDependency(processor.Processor, document.Name, dependencies);
        }

        foreach (var root in document.Roots)
        {
            CollectNodeDependencies(root.ChildList, document.Name, dependencies);
        }

        return dependencies;
    }

    private void CollectNodeDependencies(IReadOnlyList<SchemaNode> nodes, string documentName, HashSet<string> dependencies)
    {
        foreach (var node in nodes)
        {
            AddSchemaDependency(node.TypeDef, documentName, dependencies);

            if (node is SchemaField field)
            {
                foreach (var processor in field.Processors)
                {
                    AddSchemaDependency(processor.Processor, documentName, dependencies);
                }
            }

            if (node is SchemaGroup group)
            {
                foreach (var processor in group.Processors)
                {
                    AddSchemaDependency(processor.Processor, documentName, dependencies);
                }

                CollectNodeDependencies(group.ChildList, documentName, dependencies);
            }
        }
    }

    private void AddSchemaDependency(SchemaName? schemaName, string documentName, HashSet<string> dependencies)
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

    public void AddDocument(SchemaDocument document)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }
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

    public SchemaDocument LoadFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        var document = extension switch
        {
            ".xml" => LoadFromXml(content),
            ".json" => LoadFromJson(content),
            ".yaml" or ".yml" => LoadFromYaml(content),
            _ => throw new NotSupportedException($"File extension '{extension}' is not supported.")
        };

        AddDocument(document);
        return document;
    }

    public IEnumerable<SchemaDocument> LoadAssembly(string path, string resourcePath)
    {
        var documents = new List<SchemaDocument>();

        var assembly = Assembly.LoadFile(path);
        assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePath, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .ForEach(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Resource '{name}' not found in assembly '{path}'.");
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

    public SchemaDocument LoadFromYaml(string yaml)
    {
        throw new NotImplementedException();
    }

    public SchemaDocument LoadFromBinary(Stream data)
    {
        throw new NotImplementedException();
    }

    private bool ResolveReferences(SchemaDocument document)
    {
        bool allResolved = ResolveIncludes(document);

        foreach (var group in document.Roots)
        {
            if (!ResolveReferences(document, group.ChildList))
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
                foreach (var processor in group.Processors)
                {
                    if (!TryResolveProcessorRef(document, processor))
                    {
                        allResolved = false;
                    }
                }

                if (!ResolveReferences(document, group.ChildList))
                {
                    allResolved = false;
                }
            }
        }

        schemaNodes.Clear();
        schemaNodes.AddRange(resolvedNodes);

        return allResolved;
    }

    private SchemaNode InstantiateType(SchemaNode node, SchemaTypeDef typeDef)
    {
        if (node is SchemaField fieldNode)
        {
            return new SchemaField
            {
                Name = node.Name,
                Type = typeDef.Type,
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
                ChildList = [.. repeatNode.ChildList],
                Count = repeatNode.Count,
            };
        }
        if (node is SchemaChoice choiceNode)
        {
            return new SchemaChoice
            {
                Name = node.Name,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, choiceNode.Processors),
                ChildList = [.. choiceNode.ChildList],
                SelectedIndex = choiceNode.SelectedIndex,
            };
        }
        if (node is SchemaGroup groupNode)
        {
            return new SchemaGroup
            {
                Name = node.Name,
                PropertyList = MergeProperties(typeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(typeDef.Processors, groupNode.Processors),
                ChildList = [.. groupNode.ChildList],
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
            var existingProcessor = mergedProcessors.FirstOrDefault(c => c.Processor.FullName == overrideProcessor.Processor.FullName);
            if (existingProcessor != null)
            {
                mergedProcessors.Remove(existingProcessor);
            }
            mergedProcessors.Add(overrideProcessor);
        }
        return mergedProcessors;
    }

    private bool TryResolveProcessorRef(SchemaDocument document, SchemaProcessorRef processorRef)
    {
        bool allResolved = true;

        if (document.TryFindProcessor(processorRef.Processor, out var processorDecl) ||
            TryFindTypeDef(processorRef.Processor, out var processorDeclNode))
        {
            // merge properties from decl and ref
        }
        else
        {
            allResolved = false;
        }

        return allResolved;
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

    public static bool TryFindProcessor(this SchemaDocument document, SchemaName schemaName, [NotNullWhen(true)] out SchemaProcessorRef? processor)
    {
        if (String.IsNullOrEmpty(schemaName.Namespace))
        {
            processor = document.ProcessorDefs.FirstOrDefault(n => n.Processor.Name == schemaName.Name);
            return processor != null;
        }

        processor = null;
        return false;
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
