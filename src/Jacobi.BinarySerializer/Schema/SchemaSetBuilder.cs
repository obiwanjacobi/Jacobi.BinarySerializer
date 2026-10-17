using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Jacobi.BinarySerializer.Schema.Editing;
using Jacobi.BinarySerializer.Schema.Json;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Schema;

public sealed class SchemaSetBuilder
{
    private readonly Dictionary<string, SchemaDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SchemaNode, SchemaNode> _resolvedNodeDefs = [];
    private readonly HashSet<SchemaNode> _resolvingNodeDefs = [];

    public void AddDocument(SchemaDocument document)
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

    public SchemaDocument LoadFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        var document = LoadFileContent(content, extension);
        return document;
    }

    private SchemaDocument LoadFileContent(string content, string extension)
    {
        var document = extension switch
        {
            ".xml" => LoadFromXml(content),
            ".json" => LoadFromJson(content),
            ".yaml" or ".yml" => throw new NotSupportedException("YAML requires the Jacobi.BinarySerializer.Yaml package: use SchemaSetBuilder.LoadYaml()."),
            _ => throw new NotSupportedException($"File extension '{extension}' is not supported.")
        };
        return document;
    }

    public IEnumerable<SchemaDocument> LoadFromAssembly(string path, string resourcePath)
        => LoadFromAssembly(Assembly.LoadFile(path), resourcePath);

    public IEnumerable<SchemaDocument> LoadFromAssembly(Assembly assembly, string resourcePath)
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
                var document = LoadFromBinary(name, stream);

                documents.Add(document);
            });

        return documents;
    }

    public SchemaDocument LoadFromXml(string xml)
    {
        var document = XmlSerializer.Deserialize(xml);
        AddDocument(document);
        return document;
    }

    public SchemaDocument LoadFromJson(string json)
    {
        var document = JsonSerializer.Deserialize(json);
        AddDocument(document);
        return document;
    }

    public SchemaDocument LoadFromBinary(string name, Stream data)
    {
        using var reader = new StreamReader(data);
        var content = reader.ReadToEnd();
        return LoadFileContent(content, Path.GetExtension(name));
    }

    public SchemaSet Compile()
    {
        _resolvedNodeDefs.Clear();
        _resolvingNodeDefs.Clear();
        foreach (var document in _documents.Values)
        {
            document.IsCompiled = false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in document.NodeDefs)
            {
                if (definition is not SchemaField && definition is not SchemaGroup
                    || definition is Schema || String.IsNullOrWhiteSpace(definition.Name))
                {
                    throw new InvalidOperationException($"Invalid node definition '{definition.Name}' in '{document.Name}'.");
                }
                if (!names.Add(definition.Name))
                {
                    throw new InvalidOperationException($"Duplicate node definition '{document.Name}.{definition.Name}'.");
                }
            }
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

            ValidatePackedGroups(document.NodeDefs.Select(definition => ResolveNodeDef(document, definition)));
            ValidatePackedGroups(document.Roots);
            document.RefreshIndexes();

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

        return new SchemaSet(_documents);
    }

    /// <summary>
    /// Expands short property names on processor refs to their full 'namespace.id.name' form.
    /// Names that already contain '.' are left as is. Properties of a 'ref:' are expanded when it is resolved.
    /// Properties on fields and groups are not touched.
    /// </summary>
    private static void ExpandPropertyNames(SchemaDocument document)
    {
        foreach (var nodeDef in document.NodeDefs)
        {
            ExpandPropertyNames(nodeDef);
        }

        foreach (var dataTypeDef in document.DataTypeDefs)
        {
            foreach (var processor in dataTypeDef.Processors)
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

        CollectNodeDependencies(document.NodeDefs, document.Name, dependencies);

        foreach (var dataTypeDef in document.DataTypeDefs)
        {
            AddDataTypeDependency(dataTypeDef.BasedOn, document.Name, dependencies);
            foreach (var processor in dataTypeDef.Processors)
            {
                AddSchemaDependency(processor, document.Name, dependencies);
            }
        }

        foreach (var root in document.Roots)
        {
            CollectNodeDependencies([root], document.Name, dependencies);
        }

        return dependencies;
    }

    private static void CollectNodeDependencies(IReadOnlyList<SchemaNode> nodes, string documentName, HashSet<string> dependencies)
    {
        foreach (var node in nodes)
        {
            AddSchemaDependency(node.NodeDef, documentName, dependencies);

            if (node is SchemaField field)
            {
                if (field.DataType is { } fieldDataType)
                {
                    AddDataTypeDependency(fieldDataType, documentName, dependencies);
                }
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

    private static void AddDataTypeDependency(SchemaName dataType, string documentName, HashSet<string> dependencies)
    {
        var ns = dataType.Namespace;
        if (!String.IsNullOrEmpty(ns) &&
            !String.Equals(ns, "sys", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(ns, documentName, StringComparison.OrdinalIgnoreCase))
        {
            dependencies.Add(ns);
        }
    }

    private static void AddSchemaDependency(SchemaProcessorRef processorRef, string documentName, HashSet<string> dependencies)
    {
        if (processorRef.Processor.IsReference)
        {
            AddSchemaDependency(processorRef.Processor, documentName, dependencies);
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

    private bool ResolveReferences(SchemaDocument document)
    {
        bool allResolved = ResolveIncludes(document);

        foreach (var nodeDef in document.NodeDefs)
        {
            ResolveNodeDef(document, nodeDef);
        }

        foreach (var dataTypeDef in document.DataTypeDefs)
        {
            foreach (var processor in dataTypeDef.Processors)
            {
                if (!TryResolveProcessorRef(document, processor))
                {
                    allResolved = false;
                }
            }
        }

        allResolved &= ResolveReferences(document, document.MemberList, roots: true);
        document.RefreshIndexes();

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

    private bool ResolveReferences(SchemaDocument document, List<SchemaNode> schemaNodes, bool roots = false)
    {
        bool allResolved = true;
        var resolvedNodes = new List<SchemaNode>(schemaNodes);

        // Resolve references for groups, fields and processors
        foreach (var originalNode in schemaNodes)
        {
            var node = originalNode;
            if (node.NodeDef.HasValue)
            {
                var nodeDef = FindNodeDef(document, node.NodeDef.Value, $"The {node.Kind} '{node.Name}'");
                if (nodeDef.Kind != node.Kind)
                {
                    throw new InvalidOperationException(
                        $"The {node.Kind} '{node.Name}' references the nodeDef '{nodeDef.Name}' that defines a {nodeDef.Kind}: the kinds must match.");
                }

                if (!roots || node.GetType() != typeof(SchemaGroup))
                {
                    var replacementNode = InstantiateNodeDef(node, nodeDef);
                    resolvedNodes.Replace(node, replacementNode);
                    node = replacementNode;
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

            if (node is SchemaChoice { MemberNodeDef: null, Pipeline: SchemaPipelineMode.Isolate })
            {
                throw new InvalidOperationException(
                    $"The Choice '{node.Name}' sets a pipeline mode, which is only valid for an open choice (with a memberNodeDef).");
            }

            if (node is SchemaChoice { MemberNodeDef: { } memberNodeDef })
            {
                var memberDef = FindNodeDef(document, memberNodeDef, $"The Choice '{node.Name}' (member nodeDef)");
                if (memberDef.Kind != SchemaNodeKind.Group)
                {
                    throw new InvalidOperationException(
                        $"The Choice '{node.Name}' references the member nodeDef '{memberDef.Name}' that defines a {memberDef.Kind}: it must be a Group.");
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

        for (var i = 0; i < schemaNodes.Count; i++)
        {
            if (schemaNodes[i] is SchemaField { DataType: { } fieldDataType } dataTypeField &&
                TryFindDataTypeDef(document, fieldDataType, out var defDocument, out var dataTypeDef))
            {
                schemaNodes[i] = InstantiateDataType(dataTypeField, defDocument, dataTypeDef);
            }
        }

        return allResolved;
    }

    private static void ValidatePackedGroups(IEnumerable<SchemaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is SchemaPackedGroup packed)
            {
                if (packed.ByteLength is int length && length <= 0)
                {
                    throw new InvalidOperationException($"The PackedGroup '{packed.Name}' byteLength must be positive.");
                }
                ValidatePackedMembers(packed.Name, packed.Members);
            }
            if (node is SchemaGroup group)
            {
                ValidatePackedGroups(group.Members);
            }
        }
    }

    private static void ValidatePackedMembers(string name, IReadOnlyList<SchemaNode> members)
    {
        foreach (var member in members)
        {
            if (member is not SchemaField field)
            {
                throw new InvalidOperationException($"The PackedGroup '{name}' can only contain fields; '{member.Name}' is a {member.Kind}.");
            }
            if (field.ByteLength is int or SchemaNodeRef or SchemaPubRef || field.ByteOffset is not null)
            {
                throw new InvalidOperationException($"The PackedGroup '{name}' field '{field.Name}' cannot declare byteLength or byteOffset.");
            }
        }
    }

    private bool TryFindDataTypeDef(SchemaDocument document, SchemaName dataType, [NotNullWhen(true)] out SchemaDocument? defDocument, [NotNullWhen(true)] out SchemaDataTypeDef? dataTypeDef)
    {
        var name = dataType;
        var ns = name.Namespace;
        defDocument = null;
        dataTypeDef = null;

        if (String.IsNullOrEmpty(ns) || String.Equals(ns, document.Name, StringComparison.OrdinalIgnoreCase))
        {
            defDocument = document;
        }
        else
        {
            defDocument = _documents.GetValueOrDefault(ns);
        }

        dataTypeDef = defDocument?.DataTypeDefs.FirstOrDefault(d => String.Equals(d.Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (dataTypeDef is null)
        {
            defDocument = null;
            return false;
        }

        return defDocument is not null;
    }

    private SchemaField InstantiateDataType(SchemaField field, SchemaDocument defDocument, SchemaDataTypeDef dataTypeDef)
    {
        var chain = new List<SchemaDataTypeDef>();
        var current = dataTypeDef;
        var currentDocument = defDocument;
        while (current is not null)
        {
            if (chain.Contains(current))
            {
                throw new InvalidOperationException($"Circular data type definition '{current.Name}'.");
            }
            chain.Add(current);

            if (!TryFindDataTypeDef(currentDocument, current.BasedOn, out var baseDocument, out var baseDef) ||
                ReferenceEquals(baseDef, current))
            {
                break;
            }
            current = baseDef;
            currentDocument = baseDocument;
        }

        IReadOnlyList<SchemaProcessorRef> processors = [];
        var properties = new List<SchemaProperty>();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            processors = MergeProcessors(processors, chain[i].Processors);
            properties = MergeProperties(properties, chain[i].PropertyList);
        }

        return new SchemaField
        {
            Name = field.Name,
            NodeDef = field.NodeDef,
            DataType = new SchemaName($"{defDocument.Name}{SchemaName.Separator}{dataTypeDef.Name}"),
            Value = field.Value,
            ByteLength = field.ByteLength,
            ByteOffset = field.ByteOffset,
            PropertyList = MergeProperties(properties, field.Properties),
            ProcessorsList = MergeProcessors(processors, field.Processors),
        };
    }

    private static IEnumerable<SchemaProcessorRef> ValueProcessorsOf(SchemaNode node)
        => node switch
        {
            SchemaRepeat repeat => repeat.ValueProcessors,
            SchemaChoice choice => choice.ValueProcessors,
            _ => []
        };

    private SchemaNode InstantiateNodeDef(SchemaNode node, SchemaNode nodeDef)
    {
        if (node is SchemaField fieldNode && nodeDef is SchemaField fieldDef)
        {
            return new SchemaField
            {
                Name = node.Name,
                DataType = fieldNode.DataType ?? fieldDef.DataType ?? throw new InvalidOperationException($"The nodeDef '{nodeDef.Name}' has no data type and cannot be applied to the field '{node.Name}'."),
                Value = Inherit(fieldNode.Value, fieldDef.Value),
                ByteLength = Inherit(fieldNode.ByteLength, fieldDef.ByteLength),
                ByteOffset = fieldNode.ByteOffset ?? fieldDef.ByteOffset,
                PropertyList = MergeProperties(nodeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(fieldDef.Processors, fieldNode.Processors),
            };
        }
        if (node is SchemaRepeat repeatNode && nodeDef is SchemaRepeat repeatDef)
        {
            return new SchemaRepeat
            {
                Name = node.Name,
                PropertyList = MergeProperties(nodeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(repeatDef.Processors, repeatNode.Processors),
                MemberList = [.. repeatDef.Members.Select(CloneNode), .. repeatNode.MemberList],
                Count = Inherit(repeatNode.Count, repeatDef.Count),
                ByteSize = Inherit(repeatNode.ByteSize, repeatDef.ByteSize),
                ValueProcessorsList = MergeProcessors(repeatDef.ValueProcessors, repeatNode.ValueProcessors),
            };
        }
        if (node is SchemaChoice choiceNode && nodeDef is SchemaChoice choiceDef)
        {
            return new SchemaChoice
            {
                Name = node.Name,
                PropertyList = MergeProperties(nodeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(choiceDef.Processors, choiceNode.Processors),
                MemberList = [.. choiceDef.Members.Select(CloneNode), .. choiceNode.MemberList],
                SelectedIndex = Inherit(choiceNode.SelectedIndex, choiceDef.SelectedIndex),
                MemberNodeDef = choiceNode.MemberNodeDef ?? choiceDef.MemberNodeDef,
                PipelineOverride = choiceNode.PipelineOverride ?? choiceDef.PipelineOverride,
                ByteSize = Inherit(choiceNode.ByteSize, choiceDef.ByteSize),
                ValueProcessorsList = MergeProcessors(choiceDef.ValueProcessors, choiceNode.ValueProcessors),
            };
        }
        if (node is SchemaPackedGroup packedNode && nodeDef is SchemaPackedGroup packedDef)
        {
            return new SchemaPackedGroup
            {
                Name = node.Name,
                PropertyList = MergeProperties(nodeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(packedDef.Processors, packedNode.Processors),
                MemberList = [.. packedDef.Members.Select(CloneNode), .. packedNode.MemberList],
                ByteLength = Inherit(packedNode.ByteLength, packedDef.ByteLength),
                ByteSize = Inherit(packedNode.ByteSize, packedDef.ByteSize),
            };
        }
        if (node.GetType() == typeof(SchemaGroup) && nodeDef.GetType() == typeof(SchemaGroup))
        {
            var groupNode = (SchemaGroup)node;
            var groupDef = (SchemaGroup)nodeDef;
            return new SchemaGroup
            {
                Name = node.Name,
                PropertyList = MergeProperties(nodeDef.PropertyList, node.Properties),
                ProcessorsList = MergeProcessors(groupDef.Processors, groupNode.Processors),
                MemberList = [.. groupDef.Members.Select(CloneNode), .. groupNode.MemberList],
                ByteSize = Inherit(groupNode.ByteSize, groupDef.ByteSize),
            };
        }

        throw new NotSupportedException($"Type '{nodeDef.Kind}' is not supported.");
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

    // A name without a namespace (or with the document's own name) must resolve in the local document.
    private SchemaNode FindNodeDef(SchemaDocument document, SchemaName schemaName, string referrer)
    {
        var ns = schemaName.Namespace;
        var isLocal = String.IsNullOrEmpty(ns) || String.Equals(ns, document.Name, StringComparison.OrdinalIgnoreCase);

        SchemaDocument? definingDocument = isLocal ? document : _documents.GetValueOrDefault(ns!);
        var nodeDef = definingDocument?.NodeDefs.FirstOrDefault(n =>
            String.Equals(n.Name, schemaName.Name, StringComparison.OrdinalIgnoreCase));

        if (nodeDef is null)
        {
            var fullName = $"{(isLocal ? document.Name : ns)}.{schemaName.Name}";
            throw new InvalidOperationException(definingDocument is null
                ? $"{referrer} references the nodeDef '{fullName}' but the schema '{ns}' is not known."
                : $"{referrer} references the nodeDef '{fullName}' that is not defined in the schema '{definingDocument.Name}'.");
        }

        return ResolveNodeDef(definingDocument!, nodeDef);
    }

    private static SchemaValueOrRef<T> Inherit<T>(SchemaValueOrRef<T> use, SchemaValueOrRef<T> definition)
        => use is T or SchemaNodeRef or SchemaPubRef ? use : definition;

    private static SchemaNode CloneNode(SchemaNode node)
        => EditNodeMapper.ToSchemaNode(EditNodeMapper.ToEditNode(node));

    private SchemaNode ResolveNodeDef(SchemaDocument document, SchemaNode definition)
    {
        if (_resolvedNodeDefs.TryGetValue(definition, out var resolved))
        {
            return resolved;
        }
        if (!_resolvingNodeDefs.Add(definition))
        {
            throw new InvalidOperationException($"Circular node definition '{document.Name}.{definition.Name}'.");
        }
        try
        {
            var edit = EditNodeMapper.ToEditNode(definition);
            QualifyDefinition(edit, document.Name);
            var nodes = new List<SchemaNode> { EditNodeMapper.ToSchemaNode(edit) };
            if (!ResolveReferences(document, nodes))
            {
                throw new InvalidOperationException($"Failed to resolve node definition '{document.Name}.{definition.Name}'.");
            }
            _resolvedNodeDefs.Add(definition, nodes[0]);
            return nodes[0];
        }
        finally
        {
            _resolvingNodeDefs.Remove(definition);
        }
    }

    private static void QualifyDefinition(EditNode node, string documentName)
    {
        static SchemaName Qualify(SchemaName name, string owner)
            => String.IsNullOrEmpty(name.Namespace)
                ? new SchemaName($"{(name.IsReference ? "ref:" : String.Empty)}{owner}.{name.Name}")
                : name;

        if (node.NodeDef is { } nodeDef)
        {
            node.NodeDef = Qualify(nodeDef, documentName);
        }
        if (node is EditField field && field.DataType is { } dataType)
        {
            field.DataType = Qualify(dataType, documentName);
        }
        if (node is EditChoice choice && choice.MemberNodeDef is { } memberNodeDef)
        {
            choice.MemberNodeDef = Qualify(memberNodeDef, documentName);
        }
        IEnumerable<EditProcessorRef> processors = node switch
        {
            EditField value => value.Processors,
            EditRepeat repeat => repeat.Processors.Concat(repeat.ValueProcessors),
            EditChoice value => value.Processors.Concat(value.ValueProcessors),
            EditGroup group => group.Processors,
            _ => []
        };
        foreach (var processor in processors.Where(processor => processor.Processor.IsReference))
        {
            processor.Processor = Qualify(processor.Processor, documentName);
        }
        if (node is EditGroup container)
        {
            foreach (var child in container.Members)
            {
                QualifyDefinition(child, documentName);
            }
        }
    }
}

internal static class SchemaDocumentExtensions
{
    public static bool TryFindNodeDef(this SchemaDocument document, SchemaName schemaName, [NotNullWhen(true)] out SchemaNode? node)
    {
        if (String.IsNullOrEmpty(schemaName.Namespace))
        {
            node = document.NodeDefs.FirstOrDefault(n => String.Equals(n.Name, schemaName.Name, StringComparison.OrdinalIgnoreCase));
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
