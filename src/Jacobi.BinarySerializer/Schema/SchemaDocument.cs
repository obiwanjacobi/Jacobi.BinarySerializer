namespace Jacobi.BinarySerializer.Schema;

public class Schema : SchemaGroup
{
    public Schema()
    {
        Kind = SchemaNodeKind.Schema;
    }

    public required IReadOnlyList<SchemaNodeDef> NodeDefs { get; init; }
    public IReadOnlyList<SchemaDataTypeDef> DataTypeDefs { get; init; } = [];
    public required IReadOnlyList<SchemaProcessorDef> ProcessorDefs { get; init; }

    public required IReadOnlyList<SchemaDocumentRef> Includes { get; init; }
}

public sealed class SchemaDocument : Schema
{
    public required IReadOnlyList<SchemaGroup> Roots { get; init; }

    public required IReadOnlyList<SchemaGroup> Groups { get; init; }
    public required IReadOnlyList<SchemaField> Fields { get; init; }

    /// <summary>
    /// True when all references have been resolved and the schema document is ready for use.
    /// </summary>
    public bool IsCompiled { get; internal set; }
}

public sealed class SchemaDocumentRef
{
    public required string Schema { get; init; }
    public string? Path { get; init; }

    // filled after loading the schema document references
    public SchemaDocument? SchemaDocument { get; internal set; }
}
