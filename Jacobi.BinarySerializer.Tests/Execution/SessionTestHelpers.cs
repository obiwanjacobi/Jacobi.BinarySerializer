using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Shared schema, plan and fake-processor helpers for the Writer/Reader session tests.</summary>
internal static class SessionTestHelpers
{
    public const string Ns = "st";

    public static ExecutionPlan Build(SchemaGroup root, List<string>? log = null)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(log ?? []));
        return new ExecutionPlanBuilder(manager).Build(root);
    }

    public static SchemaProcessorRef Ref(string id)
        => new() { Processor = new SchemaName($"{Ns}.{id}") };

    public static SchemaField Field(string name, SchemaDataType type = SchemaDataType.Int32)
        => new() { Name = name, Type = type };

    public static SchemaField FieldWith(string name, SchemaProcessorRef processor)
        => new() { Name = name, Type = SchemaDataType.Int32, ProcessorsList = [processor] };

    public static SchemaGroup Group(string name, SchemaProcessorRef[] processors, params SchemaNode[] children)
    {
        var group = new SchemaGroup { Name = name, ProcessorsList = [.. processors] };
        group.ChildList.AddRange(children);
        return group;
    }

    // processor ids: layout, failing (layout), scale (value), bigendian (field), stream
    private sealed class Factory(List<string> log) : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id switch
        {
            "layout" => new RecordingLayout(log, false),
            "failing" => new RecordingLayout(log, true),
            "scale" => new ScaleValue(),
            "bigendian" => new BigEndianField(),
            "stream" => new PrefixStream(),
            _ => null
        };
    }

    /// <summary>Default fixed-width layout that logs Begin/Write|Read/End; optionally fails every field.</summary>
    private sealed class RecordingLayout(List<string> log, bool fail) : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, fail ? "failing" : "layout");
        public string Name => "Recording Layout";
        public PipelineStage Stage => PipelineStage.Layout;

        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
            => log.Add($"Begin:{context.Group.Path}");

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
        {
            log.Add($"Write:{context.Field!.Path}");
            return fail
                ? WriteResult.Failure
                : ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);
        }

        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
            => log.Add($"End:{context.Group.Path}");

        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"Begin:{context.Group.Path}");

        public ReadResult Read(ref SequenceReader<byte> reader, out EncodedField outValue, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.Field!.Path}");
            if (fail)
            {
                outValue = new EncodedField(context.Field.Name, typeof(byte[]), null, 0);
                return ReadResult.Failure;
            }
            return ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, out outValue, context);
        }

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"End:{context.Group.Path}");
    }

    /// <summary>Writes value * 2, reads value / 2.</summary>
    private sealed class ScaleValue : IValueProcessor
    {
        public ProcessorKey Key => new(Ns, "scale");
        public string Name => "Scale";
        public PipelineStage Stage => PipelineStage.Semantic;

        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
            => logicalValue with { Value = (int)logicalValue.Value! * 2 };
        public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
            => logicalValue with { Value = (int)logicalValue.Value! / 2 };
    }

    /// <summary>Big-endian instead of the default little-endian.</summary>
    private sealed class BigEndianField : IFieldProcessor
    {
        public ProcessorKey Key => new(Ns, "bigendian");
        public string Name => "Big Endian";
        public PipelineStage Stage => PipelineStage.Representation;

        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public EncodedField Write(LogicalField field, FieldProcessorContext context)
        {
            DataTypeCodec.TryEncode(context.Field.Field.Type, field.Value, out var bytes);
            Array.Reverse(bytes);
            return new EncodedField(field.Name, typeof(byte[]), bytes, bytes.Length * 8);
        }

        public LogicalField Read(EncodedField field, FieldProcessorContext context)
        {
            var bytes = ((byte[])field.Value!).Reverse().ToArray();
            DataTypeCodec.TryDecode(context.Field.Field.Type, bytes, out var value);
            return new LogicalField(field.Name, value!.GetType(), value);
        }
    }

    /// <summary>Frames the payload with a leading 0xFF byte.</summary>
    private sealed class PrefixStream : IStreamProcessor
    {
        public ProcessorKey Key => new(Ns, "stream");
        public string Name => "Prefix";
        public PipelineStage Stage => PipelineStage.Stream;

        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public WriteResult Write(ref SequenceReader<byte> payloadInput, IBufferWriter<byte> transportOutput, StreamProcessorContext context)
        {
            var rest = payloadInput.UnreadSequence.ToArray();
            var span = transportOutput.GetSpan(rest.Length + 1);
            span[0] = 0xFF;
            rest.CopyTo(span[1..]);
            transportOutput.Advance(rest.Length + 1);
            payloadInput.AdvanceToEnd();
            return WriteResult.Success;
        }

        public ReadResult Read(ref SequenceReader<byte> transportInput, IBufferWriter<byte> payloadOutput, StreamProcessorContext context)
        {
            transportInput.Advance(1);
            var rest = transportInput.UnreadSequence.ToArray();
            rest.CopyTo(payloadOutput.GetSpan(rest.Length));
            payloadOutput.Advance(rest.Length);
            transportInput.AdvanceToEnd();
            return ReadResult.Success;
        }
    }
}

/// <summary>Dictionary-backed value source (keyed by node path); records group entries.</summary>
internal sealed class DictSource(Dictionary<string, object?> values, List<string>? events = null) : IValueSource
{
    public bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
    {
        if (values.TryGetValue(context.Path, out var value))
        {
            field = new LogicalField(context.Name, value?.GetType() ?? typeof(object), value);
            return true;
        }
        field = null;
        return false;
    }

    public IValueSource EnterGroup(GroupContext context)
    {
        events?.Add($"Enter:{context.Path}");
        return this;
    }

    public int GetCount(RepeatContext context) => throw new NotSupportedException();
    public IValueSource EnterItem(RepeatContext context, int index) => throw new NotSupportedException();
    public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
    public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
}

/// <summary>Dictionary-backed value sink (keyed by node path); records group entries and completions.</summary>
internal sealed class DictSink : IValueSink
{
    private readonly DictSink _root;
    private readonly string _path;

    public DictSink() { _root = this; _path = "Root"; }
    private DictSink(DictSink root, string path) { _root = root; _path = path; }

    public Dictionary<string, object?> Values { get; } = [];
    public List<string> Events { get; } = [];

    public void SetField(FieldContext context, LogicalField value) => _root.Values[context.Path] = value.Value;

    public IValueSink EnterGroup(GroupContext context)
    {
        _root.Events.Add($"Enter:{context.Path}");
        return new DictSink(_root, context.Path);
    }

    public IValueSink EnterItem(RepeatContext context, int index, int count)
    {
        _root.Events.Add($"Item:{context.Path}[{index}/{count}]");
        return new DictSink(_root, $"{context.Path}[{index}]");
    }
    public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => throw new NotSupportedException();
    public void Complete() => _root.Events.Add($"Complete:{_path}");
}
