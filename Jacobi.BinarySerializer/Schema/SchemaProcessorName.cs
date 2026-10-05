using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// The name of a processor in a schema: either a processor 'namespace.id' (for example 'sys.varint'),
/// or a reference to a processor definition: 'ref:name'.
/// </summary>
public readonly struct SchemaProcessorName : IEquatable<SchemaProcessorName>, IEquatable<string>
{
    public const string RefPrefix = "ref:";

    public SchemaProcessorName()
        => throw new InvalidOperationException("SchemaProcessorName must be initialized with a name.");

    public SchemaProcessorName(string moniker)
    {
        if (moniker.StartsWith(RefPrefix, StringComparison.Ordinal))
        {
            moniker = moniker[RefPrefix.Length..];
            IsReference = true;
        }

        var i = moniker.LastIndexOf(ProcessorKey.Separator);
        if (i == -1)
        {
            Name = moniker;
        }
        else
        {
            Namespace = moniker[..i];
            Name = moniker[(i + 1)..];
        }
    }

    public string Name { get; }
    public string Namespace { get; } = String.Empty;

    /// <summary>The name without the 'ref:' prefix.</summary>
    public string FullName
        => String.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}{ProcessorKey.Separator}{Name}";

    /// <summary>True for 'ref:name': a reference to a processor definition instead of a processor.</summary>
    public bool IsReference { get; }

    /// <summary>The processor this name designates. Not valid for references or names without a namespace.</summary>
    public ProcessorKey ToProcessorKey()
        => IsReference || String.IsNullOrEmpty(Namespace)
            ? throw new InvalidOperationException($"'{this}' does not designate a processor ('namespace{ProcessorKey.Separator}id').")
            : new ProcessorKey(Namespace, Name);

    /// <summary>The name as a <see cref="SchemaName"/> (to look up definitions).</summary>
    public SchemaName ToSchemaName() => new(ToString());

    /// <summary>The name as written in a schema (including the 'ref:' prefix).</summary>
    public override string ToString()
        => IsReference ? RefPrefix + FullName : FullName;

    public bool Equals(SchemaProcessorName other)
        => IsReference == other.IsReference && String.Equals(FullName, other.FullName, StringComparison.Ordinal);
    public bool Equals(string? other) => String.Equals(ToString(), other, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj switch
    {
        SchemaProcessorName name => Equals(name),
        string text => Equals(text),
        _ => false
    };
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(SchemaProcessorName left, SchemaProcessorName right) => left.Equals(right);
    public static bool operator !=(SchemaProcessorName left, SchemaProcessorName right) => !left.Equals(right);

    public static implicit operator SchemaProcessorName(string value) => new(value);
    public static implicit operator string(SchemaProcessorName name) => name.ToString();
}
