using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>'ref:Root.Rows[2].Len' and 'ref:Root.Rows[].Len': node references that target an instance of a repeat.</summary>
public class InstanceReferenceTests
{
    [Test]
    public void Parse_WithoutIndices_HasNoIndices()
    {
        Assert.That(SchemaNodeRef.TryParse("ref:Root.Rows.Len", out var nodeRef), Is.True);
        Assert.That(nodeRef!.Path, Is.EqualTo("Root.Rows.Len"));
        Assert.That(nodeRef.Indices, Is.Empty);
    }

    [Test]
    public void Parse_ExplicitAndCurrentIndices()
    {
        Assert.That(SchemaNodeRef.TryParse("ref:Root.Grid[2].Cells[].Len", out var nodeRef), Is.True);
        Assert.That(nodeRef!.Path, Is.EqualTo("Root.Grid.Cells.Len"));
        Assert.That(nodeRef.Indices, Is.EqualTo(new[]
        {
            new SchemaInstanceIndex("Root.Grid", 2),
            new SchemaInstanceIndex("Root.Grid.Cells", SchemaInstanceIndex.Current),
        }));
        Assert.That(nodeRef.ToString(), Is.EqualTo("ref:Root.Grid[2].Cells[].Len"));
    }

    [TestCase("ref:Root.Rows[x].Len")]
    [TestCase("ref:Root.Rows[-1].Len")]
    [TestCase("ref:Root.Rows[1.Len")]
    [TestCase("ref:Root.[1].Len")]
    [TestCase("ref:Root.Rows]1[.Len")]
    public void Parse_InvalidIndex_Fails(string text)
        => Assert.That(SchemaNodeRef.TryParse(text, out _), Is.False);

    [Test]
    public void InstancePath_ResolveRelative_ReplacesCurrentMarkers()
    {
        var template = new InstancePath([InstancePath.Current, 3]);

        var resolved = template.ResolveRelative(new InstancePath([5, 9]));

        Assert.That(resolved, Is.EqualTo(new InstancePath([5, 3])));
        Assert.That(template.IsRelative, Is.True);
        Assert.That(resolved.IsRelative, Is.False);
    }

    // Root { Lens: repeat(2) { Len }, Items: repeat(<count>) { Item:Int16 } }
    private static ExecutionPlan IndexedPlan(string countRef)
    {
        SchemaNodeRef.TryParse(countRef, out var nodeRef);
        var lens = new SchemaRepeat { Name = "Lens", Count = 2 };
        lens.ChildList.Add(Field("Len"));
        var items = new SchemaRepeat { Name = "Items", Count = nodeRef! };
        items.ChildList.Add(Field("Item", SchemaDataType.Int16));
        return Build(Group("Root", [], lens, items));
    }

    [Test]
    public void Build_NoIndex_BindsTheFirstItem()
    {
        var plan = IndexedPlan("ref:Root.Lens.Len");

        var items = (RepeatInfo)plan.Root.Children[1];
        Assert.That(items.Count.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Lens.Len", new InstancePath([0]))));
    }

    [Test]
    public void Build_ExplicitIndex_BindsThatItem()
    {
        var plan = IndexedPlan("ref:Root.Lens[1].Len");

        var items = (RepeatInfo)plan.Root.Children[1];
        Assert.That(items.Count.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Lens.Len", new InstancePath([1]))));
    }

    [Test]
    public void Build_CurrentIndexOutsideTheRepeat_Throws()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => IndexedPlan("ref:Root.Lens[].Len"));

        Assert.That(ex!.Errors[0], Does.Contain("[]").And.Contain("Root.Lens"));
    }

    [Test]
    public void Build_IndexOnANodeThatIsNotARepeatOnThePath_Throws()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => IndexedPlan("ref:Root[1].Lens.Len"));

        Assert.That(ex!.Errors[0], Does.Contain("is not a repeat"));
    }

    [Test]
    public void Read_ExplicitIndex_UsesThatItemsValue()
    {
        var plan = IndexedPlan("ref:Root.Lens[1].Len");
        // Len[0]=1, Len[1]=3, then three Int16 items.
        var bytes = new byte[] { 1, 0, 0, 0, 3, 0, 0, 0, 10, 0, 20, 0, 30, 0 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Items.Item"], Is.EqualTo((short)30));
    }

    [Test]
    public void Read_NoIndex_UsesTheFirstItemsValue()
    {
        var plan = IndexedPlan("ref:Root.Lens.Len");
        var bytes = new byte[] { 1, 0, 0, 0, 3, 0, 0, 0, 10, 0 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Items.Item"], Is.EqualTo((short)10));
    }

    // Root { Rows: repeat(2) { Len, Vals: repeat(ref:Root.Rows[].Len) { Item:Int16 } } }
    private static ExecutionPlan RowsPlan()
    {
        SchemaNodeRef.TryParse("ref:Root.Rows[].Len", out var nodeRef);
        var vals = new SchemaRepeat { Name = "Vals", Count = nodeRef! };
        vals.ChildList.Add(Field("Item", SchemaDataType.Int16));
        var rows = new SchemaRepeat { Name = "Rows", Count = 2 };
        rows.ChildList.Add(Field("Len"));
        rows.ChildList.Add(vals);
        return Build(Group("Root", [], rows));
    }

    [Test]
    public void Read_CurrentIndex_UsesTheCountOfTheSameRow()
    {
        var plan = RowsPlan();
        // row 0: Len=1, one item (5); row 1: Len=2, items (6, 7).
        var bytes = new byte[] { 1, 0, 0, 0, 5, 0, 2, 0, 0, 0, 6, 0, 7, 0 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Rows.Vals.Item"], Is.EqualTo((short)7));
    }

    [Test]
    public void Read_CurrentIndex_ShortInputNeedsMoreData()
    {
        var plan = RowsPlan();
        // row 1 announces two items but only one is present.
        var bytes = new byte[] { 1, 0, 0, 0, 5, 0, 2, 0, 0, 0, 6, 0 };

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new DictSink());

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.NeedMoreData));
    }

    [Test]
    public void Write_ExplicitIndexNotPublished_Throws()
    {
        SchemaNodeRef.TryParse("ref:Root.Lens[5].Len", out var nodeRef);
        var lens = new SchemaRepeat { Name = "Lens", Count = 0 };
        lens.ChildList.Add(Field("Len"));
        var items = new SchemaRepeat { Name = "Items", Count = nodeRef! };
        items.ChildList.Add(Field("Item", SchemaDataType.Int16));
        var plan = Build(Group("Root", [], lens, items));
        var source = new DictSource(new());

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Lens.Len[5]").And.Contain("not published"));
    }

    [Test]
    public void Xml_And_Json_Mappers_PreserveInstanceIndices()
    {
        var xml = """
            <schema name="IdxSchema">
              <children>
                <group name="Root">
                  <children>
                    <repeat name="Items">
                      <count ref="Root.Lens[].Len" />
                      <children>
                        <field name="Item" type="Int32" />
                      </children>
                    </repeat>
                  </children>
                </group>
              </children>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var serialized = Jacobi.BinarySerializer.Schema.Xml.XmlSerializer.Serialize(document);
        var repeat = new SchemaSet().LoadFromXml(serialized).Roots.OfType<SchemaGroup>().Single()
            .Children.OfType<SchemaRepeat>().Single();

        Assert.That(serialized, Does.Contain("ref=\"Root.Lens[].Len\""));
        Assert.That(repeat.Count is SchemaNodeRef r && r.Indices.Single().IsCurrent);

        var json = Jacobi.BinarySerializer.Schema.Json.JsonSerializer.Serialize(document);
        var jsonRepeat = Jacobi.BinarySerializer.Schema.Json.JsonSerializer.Deserialize(json)
            .Roots.OfType<SchemaGroup>().Single().Children.OfType<SchemaRepeat>().Single();

        Assert.That(json, Does.Contain("\"Ref\": \"Root.Lens[].Len\""));
        Assert.That(jsonRepeat.Count is SchemaNodeRef jr && jr.Indices.Single().IsCurrent);
    }
}
