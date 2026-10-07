using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class SessionStateTests
{
    private static ReaderSession CreateSession()
    {
        var group = new SchemaGroup { Name = "Root" };
        group.MemberList.Add(SessionTestHelpers.Field("A"));
        return new ReaderSession(SessionTestHelpers.Build(group));
    }

    [Test]
    public void GetOrCreate_TwoStateTypesForOneBinding_AreSeparate()
    {
        var state = CreateSession();
        var binding = new ProcessorBinding(new StubProcessor(), []);

        var a = state.GetOrCreate<StateA>(binding);
        var b = state.GetOrCreate<StateB>(binding);

        Assert.That(state.GetOrCreate<StateA>(binding), Is.SameAs(a));
        Assert.That(state.GetOrCreate<StateB>(binding), Is.SameAs(b));
    }

    [Test]
    public void GetOrCreate_ReportsExists_OnlyOnSecondCall()
    {
        var state = CreateSession();
        var binding = new ProcessorBinding(new StubProcessor(), []);

        state.GetOrCreate<StateA>(binding, default, out var first);
        state.GetOrCreate<StateA>(binding, default, out var second);

        Assert.That(first, Is.False);
        Assert.That(second, Is.True);
    }

    [Test]
    public void GetOrCreate_InstanceScope_SeparatesRepeatInstances()
    {
        var state = CreateSession();
        var binding = new ProcessorBinding(new StubProcessor(), []);
        var i0 = InstancePath.Empty.Append(0);
        var i1 = InstancePath.Empty.Append(1);

        var a0 = state.GetOrCreate<StateA>(binding, i0);
        var a1 = state.GetOrCreate<StateA>(binding, i1);

        Assert.That(a1, Is.Not.SameAs(a0));
        Assert.That(state.GetOrCreate<StateA>(binding, InstancePath.Empty.Append(0)), Is.SameAs(a0));
        Assert.That(state.GetOrCreate<StateA>(binding), Is.Not.SameAs(a0));
    }

    [Test]
    public void GetOrCreate_SameStateTypeForTwoBindings_AreSeparate()
    {
        var state = CreateSession();
        var processor = new StubProcessor();
        var binding1 = new ProcessorBinding(processor, []);
        var binding2 = new ProcessorBinding(processor, []);

        Assert.That(state.GetOrCreate<StateA>(binding2), Is.Not.SameAs(state.GetOrCreate<StateA>(binding1)));
    }

    private sealed class StateA { }
    private sealed class StateB { }

    private sealed class StubProcessor : IProcessor
    {
        public ProcessorKey Key => new("st", "stub");
        public string Name => "Stub";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
    }
}
