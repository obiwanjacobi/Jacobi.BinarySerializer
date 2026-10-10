using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class SchemaSetTests
{
    [Test]
    public void Compile_WithoutDependencies_MarksDocumentAsCompiled()
    {
        var schemaSet = new SchemaSet();
        var document = CreateDocument("Main");
        schemaSet.AddDocument(document);

        schemaSet.Compile();

        Assert.That(document.IsCompiled, Is.True);
    }

    [Test]
    public void Compile_WithInclude_ResolvesIncludedDocumentReference()
    {
        var schemaSet = new SchemaSet();
        var shared = CreateDocument("Shared");
        var main = CreateDocument(
            "Main",
            includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        schemaSet.AddDocument(shared);
        schemaSet.AddDocument(main);

        schemaSet.Compile();

        Assert.That(shared.IsCompiled, Is.True);
        Assert.That(main.IsCompiled, Is.True);
        Assert.That(main.Includes.Single().SchemaDocument, Is.SameAs(shared));
    }

    [Test]
    public void Compile_WithLocalNodeDef_ReplacesReferencedFieldDefinition()
    {
        var schemaSet = new SchemaSet();

        var commonNodeDef = new SchemaNodeDef
        {
            Name = "CommonField",
            DataType = "Int32",
            Processors = []
        };

        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Value",
            DataType = "UInt8",
            NodeDef = new SchemaName("CommonField"),
            ProcessorsList = []
        });

        var main = CreateDocument("Main", roots: [rootGroup], nodeDefs: [commonNodeDef]);

        schemaSet.AddDocument(main);

        schemaSet.Compile();

        var resolvedField = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(main.IsCompiled, Is.True);
        Assert.That(resolvedField.DataType, Is.EqualTo(new SchemaDataType("Int32")));
    }

    [Test]
    public void Compile_FieldWithDataTypeDef_QualifiesDataTypeAndMergesProcessors()
    {
        var schemaSet = new SchemaSet();
        var dataTypeDef = new SchemaDataTypeDef
        {
            Name = "Celsius",
            BasedOn = "Int32",
            Scale = 100m,
            Processors = [new SchemaProcessorRef { Processor = new SchemaProcessorName("sys.scale") }]
        };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Temp",
            DataType = "Celsius",
            ProcessorsList = []
        });

        schemaSet.AddDocument(CreateDocument("Main", roots: [rootGroup], dataTypeDefs: [dataTypeDef]));
        schemaSet.Compile();

        var field = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(field.DataType.FullName, Is.EqualTo("Main.Celsius").IgnoreCase);
        Assert.That(field.Processors, Has.Count.EqualTo(1));
    }

    [Test]
    public void Compile_NodeDefInstantiation_KeepsTheFieldValue()
    {
        var schemaSet = new SchemaSet();
        var nodeDef = new SchemaNodeDef { Name = "CommonField", DataType = "Int32", Processors = [] };
        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Value",
            DataType = "UInt8",
            Value = "0x2A",
            NodeDef = new SchemaName("CommonField"),
            ProcessorsList = []
        });
        schemaSet.AddDocument(CreateDocument("Main", roots: [rootGroup], nodeDefs: [nodeDef]));

        schemaSet.Compile();

        Assert.That(rootGroup.Members.OfType<SchemaField>().Single().Value is "0x2A", Is.True);
    }

    [Test]
    public void Compile_WithMissingDependency_ThrowsInvalidOperationException()
    {
        var schemaSet = new SchemaSet();
        var main = CreateDocument(
            "Main",
            includes: [new SchemaDocumentRef { Schema = "Missing" }]);

        schemaSet.AddDocument(main);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaSet.Compile());

        Assert.That(ex!.Message, Does.Contain("Missing schema dependencies"));
        Assert.That(main.IsCompiled, Is.False);
    }

    [Test]
    public void Compile_WithCircularIncludes_ThrowsInvalidOperationException()
    {
        var schemaSet = new SchemaSet();

        var docA = CreateDocument(
            "A",
            includes: [new SchemaDocumentRef { Schema = "B" }]);
        var docB = CreateDocument(
            "B",
            includes: [new SchemaDocumentRef { Schema = "A" }]);

        schemaSet.AddDocument(docA);
        schemaSet.AddDocument(docB);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaSet.Compile());

        Assert.That(ex!.Message, Does.Contain("Circular schema dependencies"));
        Assert.That(docA.IsCompiled, Is.False);
        Assert.That(docB.IsCompiled, Is.False);
    }

    [Test]
    public void Compile_ProcessorRefProperties_AreExpandedToFullNames()
    {
        var schemaSet = new SchemaSet();
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("sys.align") };
        processor.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "4" });
        processor.PropertyList.Add(new SchemaProperty { Name = "sys.align.relative", Value = "root" });
        root.ProcessorsList.Add(processor);

        var fieldProperty = new SchemaProperty { Name = "bits", Value = "3" };
        var field = new SchemaField
        {
            Name = "A",
            DataType = "UInt8",
            ProcessorsList = [],
            PropertyList = [fieldProperty]
        };
        AddChild(root, field);

        schemaSet.AddDocument(CreateDocument("sys"));
        schemaSet.AddDocument(CreateDocument("Main", roots: [root]));

        schemaSet.Compile();

        Assert.That(processor.Properties.Select(p => p.Name),
            Is.EqualTo(new[] { "sys.align.bytes", "sys.align.relative" }));
        Assert.That(field.Properties.Single().Name, Is.EqualTo("bits"));
    }

    [Test]
    public void Compile_LocalProcessorAlias_ResolvesDefinition()
    {
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("ref:aligned") };
        root.ProcessorsList.Add(processor);
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };

        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", roots: [root], processorDefs: [def]));
        schemaSet.Compile();

        Assert.That(processor.Definition, Is.SameAs(def));
        Assert.That(processor.Key, Is.EqualTo(new ProcessorKey("sys.align")));
    }

    [Test]
    public void Compile_CrossDocumentProcessorAlias_ResolvesDefinition()
    {
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };
        var shared = CreateDocument("Shared", processorDefs: [def]);

        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("ref:Shared.aligned") };
        root.ProcessorsList.Add(processor);
        var main = CreateDocument("Main", roots: [root], includes: [new SchemaDocumentRef { Schema = "Shared" }]);

        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(shared);
        schemaSet.AddDocument(main);
        schemaSet.Compile();

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
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("ref:aligned") };
        processor.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "8" });
        root.ProcessorsList.Add(processor);

        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", roots: [root], processorDefs: [def]));
        schemaSet.Compile();

        var effective = processor.EffectiveProperties.ToDictionary(p => p.Name, p => p.Value);
        Assert.That(effective["sys.align.bytes"], Is.EqualTo("8"));
        Assert.That(effective["sys.align.relative"], Is.EqualTo("root"));
    }

    [Test]
    public void Compile_NodeDefProcessorAlias_ResolvesDefinition()
    {
        var def = new SchemaProcessorDef { Name = "aligned", Processor = new ProcessorKey("sys.align") };
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("ref:aligned") };
        var nodeDef = new SchemaNodeDef
        {
            Name = "CommonField",
            DataType = "Int32",
            Processors = [processor]
        };

        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", nodeDefs: [nodeDef], processorDefs: [def]));
        schemaSet.Compile();

        Assert.That(processor.Definition, Is.SameAs(def));
    }

    [Test]
    public void Compile_UnresolvedProcessorAlias_Throws()
    {
        var root = CreateGroup("Root");
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName("ref:missing") };
        root.ProcessorsList.Add(processor);

        var schemaSet = new SchemaSet();
        var main = CreateDocument("Main", roots: [root]);
        schemaSet.AddDocument(main);

        Assert.Throws<InvalidOperationException>(() => schemaSet.Compile());
        Assert.That(processor.Key, Is.Null);
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
