using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// The forward (open choice to bodies) and reverse (body to open choices) lookups of <see cref="SchemaSet"/> and <see cref="ExecutionPlan"/>.
/// </summary>
public class OpenChoiceQueryTests
{
    private const string ContractsSchema = """
        { "name": "Contracts", "nodeDefs": [
            { "name": "Body", "kind": "group" },
            { "name": "Part", "kind": "group" }
        ] }
        """;

    // two open choices on the same contract (integrate and isolate)
    private const string EnvelopeSchema = """
        { "name": "Envelope", "members": [
            { "name": "Root", "kind": "group", "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Contracts.Body" },
                { "name": "Raw", "kind": "choice", "memberNodeDef": "Contracts.Body", "pipeline": "isolate" }
            ] }
        ] }
        """;

    // BodyA has a nested open choice on another contract
    private const string DeviceSchema = """
        { "name": "Device", "members": [
            { "name": "BodyA", "kind": "group", "nodeDef": "Contracts.Body", "members": [
                { "name": "Id", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Sub", "kind": "choice", "memberNodeDef": "Contracts.Part" }
            ] },
            { "name": "BodyB", "kind": "group", "nodeDef": "Contracts.Body",
              "members": [ { "name": "Value", "kind": "field", "datatype": "sys.uint8" } ] },
            { "name": "PartX", "kind": "group", "nodeDef": "Contracts.Part",
              "members": [ { "name": "X", "kind": "field", "datatype": "sys.uint8" } ] },
            { "name": "Plain", "kind": "group",
              "members": [ { "name": "P", "kind": "field", "datatype": "sys.uint8" } ] }
        ] }
        """;

    // a second root with the short name 'BodyA'
    private const string VendorSchema = """
        { "name": "Vendor", "members": [
            { "name": "BodyA", "kind": "group", "nodeDef": "Contracts.Body",
              "members": [ { "name": "V", "kind": "field", "datatype": "sys.uint8" } ] }
        ] }
        """;

    // the contract is local and written unqualified
    private const string LocalSchema = """
        { "name": "Local", "nodeDefs": [ { "name": "Body", "kind": "group" } ], "members": [
            { "name": "Root", "kind": "group", "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
            ] },
            { "name": "BodyL", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ] }
        ] }
        """;

    // ---- SchemaSet --------------------------------------------------------

    [Test]
    public void SchemaSet_FindConformingGroups_ByName_FindsTheBodiesInAllDocuments()
    {
        var schemas = Compile(ContractsSchema, EnvelopeSchema, DeviceSchema, VendorSchema);

        var groups = schemas.FindConformingGroups(new SchemaName("Contracts.Body"));

        Assert.That(groups.Select(g => $"{schemas.FindDocument(g)!.Name}.{g.Name}"),
            Is.EquivalentTo(new[] { "Device.BodyA", "Device.BodyB", "Vendor.BodyA" }));
    }

    [Test]
    public void SchemaSet_FindConformingGroups_ByChoice_QualifiesALocalContract()
    {
        var schemas = Compile(LocalSchema);
        var choice = schemas.FindByPath<SchemaChoice>(new SchemaPath("Local.Root.Msg"));

        var groups = schemas.FindConformingGroups(choice);

        Assert.That(groups.Select(g => g.Name), Is.EqualTo(new[] { "BodyL" }));
    }

    [Test]
    public void SchemaSet_FindConformingGroups_ContractWithoutBodies_IsEmpty()
    {
        var schemas = Compile(EnvelopeSchema);

        Assert.That(schemas.FindConformingGroups(new SchemaName("Contracts.Body")), Is.Empty);
    }

    [Test]
    public void SchemaSet_FindConformingGroups_ClosedChoice_IsEmpty()
    {
        var schemas = Compile("""
            { "name": "Closed", "members": [
                { "name": "Root", "kind": "group", "members": [
                    { "name": "Pick", "kind": "choice", "selectedIndex": 0,
                      "members": [ { "name": "A", "kind": "field", "datatype": "sys.uint8" } ] }
                ] }
            ] }
            """);

        Assert.That(schemas.FindConformingGroups(schemas.FindByPath<SchemaChoice>(new SchemaPath("Closed.Root.Pick"))), Is.Empty);
    }

    [Test]
    public void SchemaSet_FindConformingGroups_UnqualifiedName_Throws()
    {
        var schemas = Compile(LocalSchema);

        Assert.Throws<ArgumentException>(() => schemas.FindConformingGroups(new SchemaName("Body")));
    }

    [Test]
    public void SchemaSet_FindConformingGroups_ChoiceOfOtherSet_Throws()
    {
        var schemas = Compile(LocalSchema);
        var other = Compile(LocalSchema).FindByPath<SchemaChoice>(new SchemaPath("Local.Root.Msg"));

        Assert.Throws<ArgumentException>(() => schemas.FindConformingGroups(other));
    }

    [Test]
    public void SchemaSet_FindOpenChoices_ByName_FindsNestedChoices()
    {
        var schemas = Compile(ContractsSchema, EnvelopeSchema, DeviceSchema);

        Assert.That(schemas.FindOpenChoices(new SchemaName("Contracts.Body")).Select(c => c.Name),
            Is.EqualTo(new[] { "Msg", "Raw" }));
        Assert.That(schemas.FindOpenChoices(new SchemaName("Contracts.Part")).Select(c => c.Name),
            Is.EqualTo(new[] { "Sub" }));
    }

    [Test]
    public void SchemaSet_FindOpenChoices_ByName_MatchesALocalUnqualifiedContract()
    {
        var schemas = Compile(LocalSchema);

        Assert.That(schemas.FindOpenChoices(new SchemaName("Local.Body")).Select(c => c.Name), Is.EqualTo(new[] { "Msg" }));
    }

    [Test]
    public void SchemaSet_FindOpenChoices_ByBody_UsesTheNodeDefOfTheBody()
    {
        var schemas = Compile(ContractsSchema, EnvelopeSchema, DeviceSchema);

        Assert.That(schemas.FindOpenChoices(schemas.FindRoot(new SchemaName("Device.BodyB"))).Select(c => c.Name),
            Is.EqualTo(new[] { "Msg", "Raw" }));
        Assert.That(schemas.FindOpenChoices(schemas.FindRoot(new SchemaName("Device.PartX"))).Select(c => c.Name),
            Is.EqualTo(new[] { "Sub" }));
    }

    [Test]
    public void SchemaSet_FindOpenChoices_ByBodyWithoutNodeDef_IsEmpty()
    {
        var schemas = Compile(ContractsSchema, EnvelopeSchema, DeviceSchema);

        Assert.That(schemas.FindOpenChoices(schemas.FindRoot(new SchemaName("Device.Plain"))), Is.Empty);
    }

    // ---- ExecutionPlan ----------------------------------------------------

    [Test]
    public void Plan_OpenChoices_ListsRootAndNestedChoices()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");

        // Msg and Raw build their own BodyA (integrate vs isolate), each with its own nested Sub
        Assert.That(plan.OpenChoices.Select(c => c.Path.ToString()),
            Is.EqualTo(new[] { "Root.Msg", "Root.Raw", "BodyA.Sub", "BodyA.Sub" }));
    }

    [Test]
    public void Plan_GetOpenChoiceBodies_ReturnsTheBodiesByFullName()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");
        var msg = (ChoiceInfo)plan.Find(new SchemaPath("Root.Msg"))!;

        var bodies = plan.GetOpenChoiceBodies(msg);

        Assert.That(bodies.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Device.BodyA", "Device.BodyB" }));
        Assert.That(bodies[new SchemaName("Device.BodyA")].Path.ToString(), Is.EqualTo("BodyA"));
    }

    [Test]
    public void Plan_GetOpenChoiceBodies_IntegrateAndIsolate_BuildSeparateBodies()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");
        var msg = (ChoiceInfo)plan.Find(new SchemaPath("Root.Msg"))!;
        var raw = (ChoiceInfo)plan.Find(new SchemaPath("Root.Raw"))!;

        Assert.That(plan.GetOpenChoiceBodies(raw)[new SchemaName("Device.BodyA")],
            Is.Not.SameAs(plan.GetOpenChoiceBodies(msg)[new SchemaName("Device.BodyA")]));
    }

    [Test]
    public void Plan_GetOpenChoiceBodies_ClosedOrForeignChoice_IsEmpty()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");
        var other = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");

        Assert.That(plan.GetOpenChoiceBodies((ChoiceInfo)other.Find(new SchemaPath("Root.Msg"))!), Is.Empty);
    }

    [Test]
    public void Plan_FindOpenChoices_ByFullName_ReturnsEveryChoiceThatCanSelectTheBody()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");

        Assert.That(plan.FindOpenChoices(new SchemaName("Device.BodyB")).Select(c => c.Path.ToString()),
            Is.EqualTo(new[] { "Root.Msg", "Root.Raw" }));
        Assert.That(plan.FindOpenChoices(new SchemaName("Device.PartX")).Select(c => c.Path.ToString()),
            Is.EqualTo(new[] { "BodyA.Sub", "BodyA.Sub" }));
    }

    [Test]
    public void Plan_FindOpenChoices_ByUniqueShortName_Matches()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema, VendorSchema), "Envelope.Root");

        Assert.That(plan.FindOpenChoices(new SchemaName("BodyB")), Has.Count.EqualTo(2));
    }

    [Test]
    public void Plan_FindOpenChoices_ByAmbiguousShortName_IsEmpty()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema, VendorSchema), "Envelope.Root");

        Assert.That(plan.FindOpenChoices(new SchemaName("BodyA")), Is.Empty);
        Assert.That(plan.FindOpenChoices(new SchemaName("Vendor.BodyA")), Has.Count.EqualTo(2));
    }

    [Test]
    public void Plan_FindOpenChoices_GroupThatIsNoBody_IsEmpty()
    {
        var plan = CreatePlan(Compile(ContractsSchema, EnvelopeSchema, DeviceSchema), "Envelope.Root");

        Assert.That(plan.FindOpenChoices(new SchemaName("Device.Plain")), Is.Empty);
    }

    [Test]
    public void Plan_NotCreatedFromASchemaSet_HasNoOpenChoiceRelations()
    {
        var schemas = Compile(LocalSchema);
        var plan = ExecutionPlan.Create(schemas.FindRoot(new SchemaName("Local.Root")), CreateProcessors());
        var msg = (ChoiceInfo)plan.Find(new SchemaPath("Root.Msg"))!;

        Assert.That(plan.OpenChoices, Is.Empty);
        Assert.That(plan.GetOpenChoiceBodies(msg), Is.Empty);
        Assert.That(plan.FindOpenChoices(new SchemaName("Local.BodyL")), Is.Empty);
    }

    private static SchemaSet Compile(params string[] jsons)
    {
        var builder = new SchemaSetBuilder();
        foreach (var json in jsons)
        {
            builder.LoadFromJson(json);
        }
        return builder.Compile();
    }

    private static ExecutionPlan CreatePlan(SchemaSet schemas, string root)
        => ExecutionPlan.Create(schemas, new SchemaName(root), CreateProcessors());

    private static ProcessorManager CreateProcessors()
    {
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        return manager;
    }
}
