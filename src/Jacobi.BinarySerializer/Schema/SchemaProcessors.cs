using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema;

public closed class SchemaProcessor
{
    public IReadOnlyList<SchemaProperty> Properties => PropertyList;
    internal List<SchemaProperty> PropertyList { get; init; } = [];
}

/// <summary>
/// A named processor declaration in the ProcessorDefs of a schema (document):
/// a processor key with default properties that can be referenced (by name) with a <see cref="SchemaProcessorRef"/>.
/// </summary>
public sealed class SchemaProcessorDef : SchemaProcessor
{
    /// <summary>The name used to reference this definition ('ref:name', or 'ref:document.name' from another document).</summary>
    public required string Name { get; init; }
    public required ProcessorKey Processor { get; init; }
}

/// <summary>
/// The use of a processor on a node or nodeDef: either a processor key ('namespace.id')
/// or a reference to a <see cref="SchemaProcessorDef"/> ('ref:name' or 'ref:document.name').
/// The properties of the ref override the properties of the definition.
/// </summary>
public sealed class SchemaProcessorRef : SchemaProcessor
{
    public required SchemaName Processor { get; init; }

    /// <summary>
    /// Optional namespace ('pubns') the processor publishes its values under, so published values do not collide.
    /// Not a processor property; it is interpreted by the engine. Null: the processor uses its own key as namespace.
    /// </summary>
    public string? PublishNamespace { get; init; }

    /// <summary>Filled when a reference is resolved (compiled).</summary>
    public SchemaProcessorDef? Definition { get; internal set; }

    /// <summary>The processor to use. Null when a reference is not (yet) resolved or a name has no namespace.</summary>
    public ProcessorKey? Key
        => Processor.IsReference
            ? Definition?.Processor
            : String.IsNullOrEmpty(Processor.Namespace) ? null : Processor.ToProcessorKey();

    /// <summary>The properties of the definition (if any) overridden by the properties of this ref.</summary>
    public IReadOnlyList<SchemaProperty> EffectiveProperties
    {
        get
        {
            if (Definition is null)
            {
                return Properties;
            }

            var merged = new List<SchemaProperty>(Definition.Properties);
            foreach (var property in Properties)
            {
                merged.RemoveAll(p => p.Name == property.Name);
                merged.Add(property);
            }
            return merged;
        }
    }
}

public sealed class SchemaProperty
{
    public required string Name { get; init; }
    /// <summary>
    /// The value-string is interpeted/parsed by the processor that uses this property.
    /// It may be a literal value or a reference to a processor that provides the value.
    /// Null means the property has no value (json null); a processor may give that a meaning (e.g. the default option of a map).
    /// </summary>
    public required string? Value { get; init; }
}
