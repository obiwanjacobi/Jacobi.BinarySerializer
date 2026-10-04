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
    public void Compile_WithLocalTypeDef_ReplacesReferencedFieldDefinition()
    {
        var schemaSet = new SchemaSet();

        var commonTypeDef = new SchemaTypeDef
        {
            Name = "CommonField",
            Type = SchemaDataType.Int32,
            Processors = []
        };

        var rootGroup = CreateGroup("Root");
        AddChild(rootGroup, new SchemaField
        {
            Name = "Value",
            Type = SchemaDataType.UInt8,
            TypeDef = new SchemaName("CommonField"),
            ProcessorsList = []
        });

        var main = CreateDocument("Main", roots: [rootGroup], typeDefs: [commonTypeDef]);

        schemaSet.AddDocument(main);

        schemaSet.Compile();

        var resolvedField = rootGroup.Children.OfType<SchemaField>().Single();
        Assert.That(main.IsCompiled, Is.True);
        Assert.That(resolvedField.Type, Is.EqualTo(SchemaDataType.Int32));
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
        var processor = new SchemaProcessorRef { Processor = new SchemaName("sys.align") };
        processor.PropertyList.Add(new SchemaProperty { Name = "bytes", Value = "4" });
        processor.PropertyList.Add(new SchemaProperty { Name = "sys:align.relative", Value = "root" });
        root.ProcessorsList.Add(processor);

        var fieldProperty = new SchemaProperty { Name = "bits", Value = "3" };
        var field = new SchemaField
        {
            Name = "A",
            Type = SchemaDataType.UInt8,
            ProcessorsList = [],
            PropertyList = [fieldProperty]
        };
        AddChild(root, field);

        schemaSet.AddDocument(CreateDocument("sys"));
        schemaSet.AddDocument(CreateDocument("Main", roots: [root]));

        schemaSet.Compile();

        Assert.That(processor.Properties.Select(p => p.Name),
            Is.EqualTo(new[] { "sys:align.bytes", "sys:align.relative" }));
        Assert.That(field.Properties.Single().Name, Is.EqualTo("bits"));
    }

    private static SchemaDocument CreateDocument(
        string name,
        IReadOnlyList<SchemaNode>? roots = null,
        IReadOnlyList<SchemaTypeDef>? typeDefs = null,
        IReadOnlyList<SchemaProcessorRef>? processorDefs = null,
        IReadOnlyList<SchemaDocumentRef>? includes = null)
    {
        var rootList = roots?.OfType<SchemaGroup>().ToList() ?? [];
        return new SchemaDocument
        {
            Name = name,
            Roots = rootList,
            Groups = rootList,
            Fields = rootList.SelectMany(GetFields).ToList(),
            ChildList = roots?.ToList() ?? [],
            TypeDefs = typeDefs ?? [],
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
        ((List<SchemaNode>)group.Children).Add(child);
    }

    private static IEnumerable<SchemaField> GetFields(SchemaGroup group)
    {
        foreach (var child in group.Children)
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
