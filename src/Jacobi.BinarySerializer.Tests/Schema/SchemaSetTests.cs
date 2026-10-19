using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class SchemaSetTests
{
        [Test]
        public void Compile_ConcreteDefinition_InheritsFieldLengthAndHonorsOverride()
        {
                var builder = new SchemaSetBuilder();
                var document = builder.LoadFromJson("""
                        { "name": "Doc", "nodeDefs": [
                                { "kind": "field", "name": "Bytes", "datatype": "sys.bytes", "byteLength": 2 }
                            ], "members": [
                                { "kind": "group", "name": "Root", "members": [
                                    { "kind": "field", "name": "Inherited", "nodeDef": "Bytes" },
                                    { "kind": "field", "name": "Override", "nodeDef": "Bytes", "byteLength": 3 }
                                ] }
                            ] }
                        """);
                Assert.That(document.NodeDefs.Single(), Is.TypeOf<SchemaField>());
                builder.Compile();
                Assert.That(document.Fields[0].ByteLength is int inherited && inherited == 2, Is.True);
                Assert.That(document.Fields[1].ByteLength is int overridden && overridden == 3, Is.True);
        }

    [Test]
    public void Compile_ConcreteDefinitions_InheritEveryNodeKindAndHonorZeroOverrides()
    {
        var document = new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions
                .Field("Field", "sys.bytes", value: "AA", byteLength: 2, byteOffset: -1)
                .Group("Group", members => members.Field("Inherited"), byteSize: 6)
                .Repeat("Repeat", 2, members => members.Field("Item"), byteSize: 8,
                    valueProcessors: processors => processors.Add("sys.map"))
                .Choice("Choice", 1, members => members.Field("Alternative"), byteSize: 4,
                    valueProcessors: processors => processors.Add("sys.map")))
            .Group("Root", members => members
                .Field("FieldUse", dataType: null, nodeDef: "Field", value: String.Empty, byteLength: 0, byteOffset: 0)
                .Group("GroupUse", group => group.Field("Local"), nodeDef: "Group", byteSize: 0)
                .Repeat("RepeatUse", 0, nodeDef: "Repeat", byteSize: 0)
                .Choice("ChoiceUse", 0, nodeDef: "Choice", byteSize: 0))
            .Build();
        var builder = new SchemaSetBuilder();
        builder.AddDocument(document);
        builder.Compile();
        var nodes = document.Roots.Single().Members;
        var field = (SchemaField)nodes[0];
        Assert.That(field.Value is string value && value == String.Empty, Is.True);
        Assert.That(field.ByteLength is int length && length == 0, Is.True);
        Assert.That(field.ByteOffset, Is.Zero);
        var group = (SchemaGroup)nodes[1];
        Assert.That(group.ByteSize is int size && size == 0, Is.True);
        Assert.That(group.Members.Select(member => member.Name), Is.EqualTo(new[] { "Inherited", "Local" }));
        var repeat = (SchemaRepeat)nodes[2];
        Assert.That(repeat.Count is int count && count == 0, Is.True);
        Assert.That(repeat.ByteSize is int repeatSize && repeatSize == 0, Is.True);
        Assert.That(repeat.ValueProcessors.Single().Processor.ToString(), Is.EqualTo("sys.map"));
        var choice = (SchemaChoice)nodes[3];
        Assert.That(choice.SelectedIndex is int index && index == 0, Is.True);
        Assert.That(choice.ByteSize is int choiceSize && choiceSize == 0, Is.True);
        Assert.That(choice.ValueProcessors.Single().Processor.ToString(), Is.EqualTo("sys.map"));
    }

    [Test]
    public void Compile_DefinitionChains_CloneMembersAndPreserveReferenceValues()
    {
        var reference = new SchemaNodeRef { Path = "Root.Length" };
        var document = new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions
                .Field("Base", "sys.bytes", byteLength: reference)
                .Field("Derived", dataType: null, nodeDef: "Base", value: new SchemaPubRef { Namespace = "header", Name = "value" })
                .Group("Container", members => members.Field("Value", dataType: null, nodeDef: "Derived")))
            .Group("Root", members => members
                .Field("Length")
                .Group("First", nodeDef: "Container")
                .Group("Second", nodeDef: "Container"))
            .Build();
        var builder = new SchemaSetBuilder();
        builder.AddDocument(document);
        builder.Compile();
        var root = document.Roots.Single();
        var first = (SchemaField)((SchemaGroup)root.Members[1]).Members.Single();
        var second = (SchemaField)((SchemaGroup)root.Members[2]).Members.Single();
        Assert.That(first, Is.Not.SameAs(second));
        Assert.That(first.ByteLength is SchemaNodeRef length && length.Path == "Root.Length", Is.True);
        Assert.That(first.Value is SchemaPubRef value && value.ToString() == "pub:header.value", Is.True);
        var template = (SchemaGroup)document.NodeDefs[2];
        Assert.That(template.Members.Single().NodeDef?.ToString(), Is.EqualTo("Derived"));
        Assert.That(first, Is.Not.SameAs(template.Members.Single()));
    }

    [Test]
    public void Compile_Definitions_KeepTheirDeclaringNamespace()
    {
        var shared = new SchemaDocumentBuilder("Shared")
            .DataTypeDef("Scaled", "sys.uint8")
            .ProcessorDef("Mapping", "sys.map")
            .NodeDefs(definitions => definitions.Group("Template", members => members
                .Field("Value", "Scaled", processors: processors => processors.Add("ref:Mapping"))))
            .Build();
        var main = new SchemaDocumentBuilder("Main")
            .Group("Root", members => members.Group("Use", nodeDef: "Shared.Template"))
            .Build();
        var builder = new SchemaSetBuilder();
        builder.AddDocument(main);
        builder.AddDocument(shared);
        builder.Compile();
        var field = main.Fields.Single();
        Assert.That(field.DataType?.ToString(), Is.EqualTo("Shared.Scaled"));
        Assert.That(field.Processors.Single().Definition, Is.SameAs(shared.ProcessorDefs.Single()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Compile_OpenChoiceDefinition_DistinguishesOmittedAndExplicitDefaultPipeline(bool explicitDefault)
    {
        var document = new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions.Group("Body").OpenChoice("Envelope", "Body", SchemaPipelineMode.Isolate))
            .Group("Root", members => members.Choice("Use", default, nodeDef: "Envelope",
                pipeline: explicitDefault ? SchemaPipelineMode.Integrate : null))
            .Build();
        var builder = new SchemaSetBuilder();
        builder.AddDocument(document);
        builder.Compile();
        var choice = (SchemaChoice)document.Roots.Single().Members.Single();
        Assert.That(choice.MemberNodeDef?.ToString(), Is.EqualTo("Doc.Body"));
        Assert.That(choice.Pipeline, Is.EqualTo(explicitDefault ? SchemaPipelineMode.Integrate : SchemaPipelineMode.Isolate));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Compile_CircularDefinitions_FailClearly(bool throughMembers)
    {
        var document = new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions =>
            {
                if (throughMembers)
                {
                    definitions.Group("Loop", members => members.Group("Again", nodeDef: "Loop"));
                }
                else
                {
                    definitions.Field("First", dataType: null, nodeDef: "Second")
                        .Field("Second", dataType: null, nodeDef: "First");
                }
            })
            .Group("Root")
            .Build();
        var builder = new SchemaSetBuilder();
        builder.AddDocument(document);
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains("Circular node definition"));
    }

    [Test]
    public void FindNode_NestedPath_ReturnsNode()
    {
        var root = CreateGroup("Root");
        var inner = CreateGroup("Inner");
        var leaf = CreateGroup("Leaf");
        AddChild(inner, leaf);
        AddChild(root, inner);
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root]));
        var schemaSet = schemaBuilder.Compile();

        Assert.That(schemaSet.FindByPath(new SchemaPath("Main.Root.Inner.Leaf")), Is.SameAs(leaf));
        Assert.That(schemaSet.FindByPath<SchemaGroup>(new SchemaPath("main.root.inner")), Is.SameAs(inner));
        Assert.That(schemaSet.TryFindByPath(new SchemaPath("Main.Root.Missing"), out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => schemaSet.FindByPath(new SchemaPath("Main.Root.Missing")));
    }

    [Test]
    public void FindNode_NestedName_ReturnsNode()
    {
        var root = CreateGroup("Root");
        var inner = CreateGroup("Inner");
        var leaf = CreateGroup("Leaf");
        AddChild(inner, leaf);
        AddChild(root, inner);
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root]));
        var schemaSet = schemaBuilder.Compile();

        Assert.That(schemaSet.FindByName(new SchemaName("Main.Leaf")), Is.SameAs(leaf));
        Assert.That(schemaSet.FindByName<SchemaGroup>(new SchemaName("main.inner")), Is.SameAs(inner));
        Assert.That(schemaSet.TryFindByName(new SchemaName("Main.Missing"), out _), Is.False);
        Assert.That(schemaSet.TryFindByName(new SchemaName("Other.Leaf"), out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => schemaSet.FindByName(new SchemaName("Main.Missing")));
    }

    [Test]
    public void Compile_WithoutDependencies_MarksDocumentAsCompiled()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var document = CreateDocument("Main");
        schemaBuilder.AddDocument(document);

        var schemaSet = schemaBuilder.Compile();

        Assert.That(document.IsCompiled, Is.True);
    }

    [Test]
    public void Compile_WithInclude_ResolvesIncludedDocumentReference()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var shared = CreateDocument("Shared");
        var main = CreateDocument(
            "Main",
            includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        schemaBuilder.AddDocument(shared);
        schemaBuilder.AddDocument(main);

        var schemaSet = schemaBuilder.Compile();

        Assert.That(shared.IsCompiled, Is.True);
        Assert.That(main.IsCompiled, Is.True);
        Assert.That(main.Includes.Single().SchemaDocument, Is.SameAs(shared));
    }

    [Test]
    public void Compile_WithLocalNodeDef_ReplacesReferencedFieldDefinition()
    {
        var schemaBuilder = new SchemaSetBuilder();

        var commonNodeDef = new SchemaField
        {
            Name = "CommonField",
            DataType = "sys.int32",
            ProcessorsList = []
        };

        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Value",
            DataType = "sys.uint8",
            NodeDef = new SchemaName("CommonField"),
            ProcessorsList = []
        });

        var main = CreateDocument("Main", roots: [rootGroup], nodeDefs: [commonNodeDef]);

        schemaBuilder.AddDocument(main);

        var schemaSet = schemaBuilder.Compile();

        var resolvedField = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(main.IsCompiled, Is.True);
        Assert.That(resolvedField.DataType, Is.EqualTo(new SchemaName("sys.uint8")));
    }

    [Test]
    public void Compile_FieldWithDataTypeDef_QualifiesDataTypeAndMergesProcessors()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var dataTypeDef = new SchemaDataTypeDef
        {
            Name = "Celsius",
            BasedOn = "sys.int32",
            Scale = 100m,
            Processors = [new SchemaProcessorRef { Processor = new SchemaName("sys.scale") }]
        };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Temp",
            DataType = "Celsius",
            ProcessorsList = []
        });

        schemaBuilder.AddDocument(CreateDocument("Main", roots: [rootGroup], dataTypeDefs: [dataTypeDef]));
        var schemaSet = schemaBuilder.Compile();

        var field = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(field.DataType!.Value.FullName, Is.EqualTo("Main.Celsius").IgnoreCase);
        Assert.That(field.Processors, Has.Count.EqualTo(1));
    }

    [Test]
    public void Compile_DataTypeDefBasedOnDef_MergesProcessorsAlongTheChain()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var a = new SchemaDataTypeDef
        {
            Name = "A",
            BasedOn = "sys.int32",
            Processors = [new SchemaProcessorRef { Processor = new SchemaName("sys.scale") }]
        };
        var b = new SchemaDataTypeDef
        {
            Name = "B",
            BasedOn = "Main.A",
            Processors = [new SchemaProcessorRef { Processor = new SchemaName("sys.align") }]
        };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField { Name = "Value", DataType = "Main.B", ProcessorsList = [] });

        schemaBuilder.AddDocument(CreateDocument("Main", roots: [rootGroup], dataTypeDefs: [a, b]));
        var schemaSet = schemaBuilder.Compile();

        var field = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(field.DataType!.Value.FullName, Is.EqualTo("Main.B").IgnoreCase);
        Assert.That(field.Processors.Select(p => p.Processor.FullName), Is.EquivalentTo(new[] { "sys.scale", "sys.align" }));
    }

    [Test]
    public void Compile_CircularDataTypeDefs_Throws()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "Main.B" };
        var b = new SchemaDataTypeDef { Name = "B", BasedOn = "Main.A" };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField { Name = "Value", DataType = "Main.A", ProcessorsList = [] });

        schemaBuilder.AddDocument(CreateDocument("Main", roots: [rootGroup], dataTypeDefs: [a, b]));

        Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());
    }

    [Test]
    public void Compile_DataTypeDefFromIncludedDocument_IsResolved()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var celsius = new SchemaDataTypeDef
        {
            Name = "Celsius",
            BasedOn = "sys.int32",
            Processors = [new SchemaProcessorRef { Processor = new SchemaName("sys.scale") }]
        };
        var shared = CreateDocument("Shared", dataTypeDefs: [celsius]);
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField { Name = "Temp", DataType = "Shared.Celsius", ProcessorsList = [] });
        var main = CreateDocument("Main", roots: [rootGroup], includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        schemaBuilder.AddDocument(shared);
        schemaBuilder.AddDocument(main);
        var schemaSet = schemaBuilder.Compile();

        var field = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(field.DataType!.Value.FullName, Is.EqualTo("Shared.Celsius").IgnoreCase);
        Assert.That(field.Processors, Has.Count.EqualTo(1));
    }

    [Test]
    public void Compile_NodeDefInstantiation_KeepsTheFieldValue()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var nodeDef = new SchemaField { Name = "CommonField", DataType = "sys.int32" };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Value",
            DataType = "sys.uint8",
            Value = "0x2A",
            NodeDef = new SchemaName("CommonField"),
            ProcessorsList = []
        });
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [rootGroup], nodeDefs: [nodeDef]));

        var schemaSet = schemaBuilder.Compile();

        Assert.That(rootGroup.Members.OfType<SchemaField>().Single().Value is "0x2A", Is.True);
    }

    [Test]
    public void Compile_WithMissingDependency_ThrowsInvalidOperationException()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var main = CreateDocument(
            "Main",
            includes: [new SchemaDocumentRef { Schema = "Missing" }]);

        schemaBuilder.AddDocument(main);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex!.Message, Does.Contain("Missing schema dependencies"));
        Assert.That(main.IsCompiled, Is.False);
    }

    [Test]
    public void Compile_WithCircularIncludes_ThrowsInvalidOperationException()
    {
        var schemaBuilder = new SchemaSetBuilder();

        var docA = CreateDocument(
            "A",
            includes: [new SchemaDocumentRef { Schema = "B" }]);
        var docB = CreateDocument(
            "B",
            includes: [new SchemaDocumentRef { Schema = "A" }]);

        schemaBuilder.AddDocument(docA);
        schemaBuilder.AddDocument(docB);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex!.Message, Does.Contain("Circular schema dependencies"));
        Assert.That(docA.IsCompiled, Is.False);
        Assert.That(docB.IsCompiled, Is.False);
    }

    [Test]
    public void Compile_ProcessorRefProperties_AreExpandedToFullNames()
    {
        var schemaBuilder = new SchemaSetBuilder();
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaName("sys.align") };
        processor.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "4" });
        processor.PropertyList.Add(new SchemaProperty { Name = "sys.align.relative", Value = "root" });
        root.ProcessorsList.Add(processor);

        var fieldProperty = new SchemaProperty { Name = "bits", Value = "3" };
        var field = new SchemaField
        {
            Name = "A",
            DataType = "sys.uint8",
            ProcessorsList = [],
            PropertyList = [fieldProperty]
        };
        AddChild(root, field);

        schemaBuilder.AddDocument(CreateDocument("sys"));
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root]));

        var schemaSet = schemaBuilder.Compile();

        Assert.That(processor.Properties.Select(p => p.Name),
            Is.EqualTo(new[] { "sys.align.bytes", "sys.align.relative" }));
        Assert.That(field.Properties.Single().Name, Is.EqualTo("bits"));
    }

    [Test]
    public void Compile_LocalProcessorAlias_ResolvesDefinition()
    {
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaName("ref:aligned") };
        root.ProcessorsList.Add(processor);
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };

        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root], processorDefs: [def]));
        var schemaSet = schemaBuilder.Compile();

        Assert.That(processor.Definition, Is.SameAs(def));
        Assert.That(processor.Key, Is.EqualTo(new ProcessorKey("sys.align")));
    }

    [Test]
    public void Compile_CrossDocumentProcessorAlias_ResolvesDefinition()
    {
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };
        var shared = CreateDocument("Shared", processorDefs: [def]);

        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaName("ref:Shared.aligned") };
        root.ProcessorsList.Add(processor);
        var main = CreateDocument("Main", roots: [root], includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(shared);
        schemaBuilder.AddDocument(main);
        var schemaSet = schemaBuilder.Compile();

        Assert.That(processor.Definition, Is.SameAs(def));
        Assert.That(processor.Key, Is.EqualTo(new ProcessorKey("sys.align")));
    }

    [Test]
    public void Compile_ProcessorAlias_RefPropertiesOverrideDefinition()
    {
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };
        def.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "4" });
        def.PropertyList.Add(new SchemaProperty { Name = "relative", Value = "root" });

        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaName("ref:aligned") };
        processor.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "8" });
        root.ProcessorsList.Add(processor);

        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root], processorDefs: [def]));
        var schemaSet = schemaBuilder.Compile();

        var effective = processor.EffectiveProperties.ToDictionary(p => p.Name, p => p.Value);
        Assert.That(effective["sys.align.bytes"], Is.EqualTo("8"));
        Assert.That(effective["sys.align.relative"], Is.EqualTo("root"));
    }

    [Test]
    public void Compile_NodeDefProcessorAlias_ResolvesDefinition()
    {
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };
        var processor = new SchemaProcessorRef { Processor = new SchemaName("ref:aligned") };
        var nodeDef = new SchemaField
        {
            Name = "CommonField",
            DataType = "sys.int32",
            ProcessorsList = [processor]
        };

        var schemaBuilder = new SchemaSetBuilder();
        var root = CreateGroup("Root");
        AddChild(root, new SchemaField { Name = "Value", NodeDef = new SchemaName("CommonField") });
        schemaBuilder.AddDocument(CreateDocument("Main", roots: [root], nodeDefs: [nodeDef], processorDefs: [def]));
        var schemaSet = schemaBuilder.Compile();

        Assert.That(((SchemaField)root.Members.Single()).Processors.Single().Definition, Is.SameAs(def));
        Assert.That(processor.Definition, Is.Null);
    }

    [Test]
    public void Compile_UnresolvedProcessorAlias_Throws()
    {
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaName("ref:missing") };
        root.ProcessorsList.Add(processor);

        var schemaBuilder = new SchemaSetBuilder();
        var main = CreateDocument("Main", roots: [root]);
        schemaBuilder.AddDocument(main);

        Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());
        Assert.That(processor.Key, Is.Null);
    }

    [Test]
    public void Compile_UnqualifiedNodeDef_OnlyResolvesInTheOwnDocument()
    {
        var shared = CreateDocument("Shared", nodeDefs: [new SchemaField { Name = "CommonField", DataType = "sys.int32" }]);
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField { Name = "Value", DataType = "sys.uint8", NodeDef = new SchemaName("commonfield"), ProcessorsList = [] });
        var main = CreateDocument("Main", roots: [rootGroup], includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(shared);
        schemaBuilder.AddDocument(main);
        Assert.That(() => schemaBuilder.Compile(), Throws.Exception);
    }

    private static SchemaDocument CreateDocument(
        string name,
        IReadOnlyList<SchemaNode>? roots = null,
        IReadOnlyList<SchemaNode>? nodeDefs = null,
        IReadOnlyList<SchemaDataTypeDef>? dataTypeDefs = null,
        IReadOnlyList<SchemaProcessorDef>? processorDefs = null,
        IReadOnlyList<SchemaDocumentRef>? includes = null)
    {
        var rootList = roots?.OfType<SchemaGroup>().ToList() ?? [];
        return new SchemaDocument
        {
            Name = name,
            Roots = rootList,
            Groups = rootList,
            Fields = rootList.SelectMany(GetFields).ToList(),
            MemberList = roots?.ToList() ?? [],
            NodeDefs = nodeDefs ?? [],
            DataTypeDefs = dataTypeDefs ?? [],
            ProcessorDefs = processorDefs ?? [],
            Includes = includes ?? []
        };
    }

    private static SchemaGroup CreateGroup(string name)
    {
        return new SchemaGroup
        {
            Name = name,
            ProcessorsList = []
        };
    }

    private static void AddChild(SchemaGroup group, SchemaNode child)
    {
        ((List<SchemaNode>)group.Members).Add(child);
    }

    private static IEnumerable<SchemaField> GetFields(SchemaGroup group)
    {
        foreach (var child in group.Members)
        {
            if (child is SchemaField field)
            {
                yield return field;
            }

            if (child is SchemaGroup nestedGroup)
            {
                foreach (var nestedField in GetFields(nestedGroup))
                {
                    yield return nestedField;
                }
            }
        }
    }
}

