using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Schema;

/// <summary>An unresolved reference reports the qualified path of the referring node and the name that was not found.</summary>
public class UnresolvedReferenceTests
{
    [Test]
    public void Compile_UnknownNodeDefInNestedGroup_ReportsPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "Inner", "kind": "group", "members": [
                        { "name": "A", "kind": "field", "datatype": "sys.uint8", "nodeDef": "Nope" }
                    ] }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The Field 'Doc.Root.Inner.A' references the nodeDef 'Doc.Nope' that is not defined in the schema 'Doc'."));
    }

    [Test]
    public void Compile_UnknownNodeDefInOtherSchema_ReportsPathAndName()
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson("""{ "name": "Other", "nodeDefs": [ { "name": "Word", "kind": "field", "datatype": "sys.uint16" } ] }""");
        builder.LoadFromJson("""
            { "name": "Doc", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "A", "kind": "field", "nodeDef": "Other.Nope" }
                ] }
            ] }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Compile());

        Assert.That(ex.Message, Is.EqualTo(
            "The Field 'Doc.Root.A' references the nodeDef 'Other.Nope' that is not defined in the schema 'Other'."));
    }

    [Test]
    public void Compile_UnknownMemberNodeDef_ReportsPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "Msg", "kind": "choice", "memberNodeDef": "Nope" }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Does.Contain("'Doc.Root.Msg' (member nodeDef)").And.Contain("'Doc.Nope'"));
    }

    [Test]
    public void Compile_UnknownDataType_ReportsPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "A", "kind": "field", "datatype": "point" }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The Field 'Doc.Root.A' references the data type 'Doc.point' that is not defined in the schema 'Doc'."));
    }

    [Test]
    public void Compile_UnknownDataTypeInsideNodeDef_ReportsNodeDefPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "nodeDefs": [
                { "name": "Header", "kind": "group", "members": [
                    { "name": "A", "kind": "field", "datatype": "point" }
                ] }
            ], "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "Msg", "kind": "group", "nodeDef": "Header" }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The Field 'Doc.Header.A' references the data type 'Doc.point' that is not defined in the schema 'Doc'."));
    }

    [Test]
    public void Compile_UnknownBasedOn_ReportsDataTypeDefAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "dataTypeDefs": [ { "name": "Point", "basedOn": "Nope" } ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The dataTypeDef 'Doc.Point' is based on the data type 'Doc.Nope' that is not defined in the schema 'Doc'."));
    }

    [Test]
    public void Compile_UnknownProcessorDef_ReportsPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "A", "kind": "field", "datatype": "sys.uint8", "processors": [ { "name": "nope" } ] }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The Field 'Doc.Root.A' references the processor 'Doc.nope' that is not defined in the schema 'Doc'."));
    }

    [Test]
    public void Compile_UnknownProcessorDefInsideNodeDef_ReportsNodeDefPathAndName()
    {
        var ex = Compile("""
            { "name": "Doc", "nodeDefs": [
                { "name": "Header", "kind": "group", "processors": [ { "name": "nope" } ] }
            ], "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "Msg", "kind": "group", "nodeDef": "Header" }
                ] }
            ] }
            """);

        Assert.That(ex.Message, Is.EqualTo(
            "The Group 'Doc.Header' references the processor 'Doc.nope' that is not defined in the schema 'Doc'."));
    }

    private static InvalidOperationException Compile(string json)
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson(json);
        return Assert.Throws<InvalidOperationException>(() => builder.Compile());
    }
}
