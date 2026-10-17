using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class SchemaSetTests
{
        [Test]
        public void Compile_NodeDefByteLength_IsInheritedUnlessFieldOverridesIt()
        {
                var builder = new SchemaSetBuilder();
                var document = builder.LoadFromXml("""
                        <schema name="Test">
                            <nodeDefs><nodeDef name="Payload" kind="field" datatype="sys.bytes" byteLength="2" /></nodeDefs>
                            <members><group name="Root"><members>
                                <field name="Inherited" nodeDef="Payload" />
                                <field name="Override" nodeDef="Payload" byteLength="3" />
                            </members></group></members>
                        </schema>
                        """);

                builder.Compile();

                var inherited = document.Fields.Single(field => field.Name == "Inherited");
                var overridden = document.Fields.Single(field => field.Name == "Override");
                Assert.That(inherited.ByteLength is int length && length == 2, Is.True);
                Assert.That(overridden.ByteLength is int overrideLength && overrideLength == 3, Is.True);
                Assert.That(inherited.Properties.Any(property => property.Name == "byteLength"), Is.False);
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

        var commonNodeDef = new SchemaNodeDef
        {
            Name = "CommonField",
            Kind = SchemaNodeKind.Field,
            DataType = "sys.int32",
            Processors = []
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
        Assert.That(resolvedField.DataType, Is.EqualTo(new SchemaName("sys.int32")));
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
        var nodeDef = new SchemaNodeDef { Name = "CommonField", Kind = SchemaNodeKind.Field, DataType = "sys.int32", Processors = [] };
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
        var nodeDef = new SchemaNodeDef
        {
            Name = "CommonField",
            Kind = SchemaNodeKind.Field,
            DataType = "sys.int32",
            Processors = [processor]
        };

        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(CreateDocument("Main", nodeDefs: [nodeDef], processorDefs: [def]));
        var schemaSet = schemaBuilder.Compile();

        Assert.That(processor.Definition, Is.SameAs(def));
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
        var shared = CreateDocument("Shared", nodeDefs: [new SchemaNodeDef { Name = "CommonField", Kind = SchemaNodeKind.Field, DataType = "sys.int32", Processors = [] }]);
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
        IReadOnlyList<SchemaNodeDef>? nodeDefs = null,
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

