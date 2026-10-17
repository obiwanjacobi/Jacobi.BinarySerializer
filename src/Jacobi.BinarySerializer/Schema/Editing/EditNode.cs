namespace Jacobi.BinarySerializer.Schema.Editing;

public abstract class EditNode
{
    public string Name { get; set; } = String.Empty;
    public SchemaName? NodeDef { get; set; }
    public List<EditProperty> Properties { get; } = [];
}

public sealed class EditField : EditNode
{
    public SchemaName? DataType { get; set; }
    public SchemaValueOrRef<string> Value { get; set; }
    public SchemaValueOrRef<int> ByteLength { get; set; }
    public int? ByteOffset { get; set; }
    public List<EditProcessorRef> Processors { get; } = [];
}

public class EditGroup : EditNode
{
    public SchemaValueOrRef<int> ByteSize { get; set; }
    public List<EditNode> Members { get; } = [];
    public List<EditProcessorRef> Processors { get; } = [];
}

public sealed class EditPackedGroup : EditGroup
{
    public SchemaValueOrRef<int> ByteLength { get; set; }
}

public sealed class EditRepeat : EditGroup
{
    public SchemaValueOrRef<int> Count { get; set; }
    public List<EditProcessorRef> ValueProcessors { get; } = [];
}

public sealed class EditChoice : EditGroup
{
    public SchemaValueOrRef<int> SelectedIndex { get; set; }
    public SchemaName? MemberNodeDef { get; set; }
    public SchemaPipelineMode? Pipeline { get; set; }
    public List<EditProcessorRef> ValueProcessors { get; } = [];
}

public sealed class EditDocument : EditGroup
{
    public List<EditNode> NodeDefs { get; } = [];
    public List<EditDataTypeDef> DataTypeDefs { get; } = [];
    public List<EditProcessorDef> ProcessorDefs { get; } = [];
    public List<EditDocumentRef> Includes { get; } = [];
}

public sealed class EditDataTypeDef
{
    public string Name { get; set; } = String.Empty;
    public required SchemaName BasedOn { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Scale { get; set; }
    public decimal? Shift { get; set; }
    public Dictionary<long, string>? Options { get; set; }
    public List<EditProcessorRef> Processors { get; } = [];
    public List<EditProperty> Properties { get; } = [];
}

public sealed class EditProcessorRef
{
    public required SchemaName Processor { get; set; }
    public string? PublishNamespace { get; set; }
    public List<EditProperty> Properties { get; } = [];
}

public sealed class EditProcessorDef
{
    public string Name { get; set; } = String.Empty;
    public string Processor { get; set; } = String.Empty;
    public List<EditProperty> Properties { get; } = [];
}

public sealed class EditProperty
{
    public string Name { get; set; } = String.Empty;
    public string? Value { get; set; }
}

public sealed class EditDocumentRef
{
    public string Schema { get; set; } = String.Empty;
    public string? Path { get; set; }
}