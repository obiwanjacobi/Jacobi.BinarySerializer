using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// A namespaced name in a schema: 'namespace.name', or a reference 'ref:name'.
/// Names are compared case-insensitively.
/// </summary>
public readonly struct SchemaName
{
    public const char Separator = '.';

    public SchemaName()
        => throw new InvalidOperationException("SchemaName must be initialized with a name.");

    public SchemaName(string moniker)
    {
        if (moniker.StartsWith("ref:", StringComparison.Ordinal))
        {
            moniker = moniker[4..];
            IsReference = true;
        }

        var i = moniker.LastIndexOf(Separator);
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
    public string FullName
        => String.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";

    public bool IsReference { get; }

    /// <summary>The processor this name designates. Not valid for references or names without a namespace.</summary>
    public ProcessorKey ToProcessorKey()
        => IsReference || String.IsNullOrEmpty(Namespace)
            ? throw new InvalidOperationException($"'{this}' does not designate a processor ('namespace{Separator}id').")
            : new ProcessorKey(Namespace, Name);

    /// <summary>The name as written in a schema (including the 'ref:' prefix).</summary>
    public override string ToString()
        => IsReference ? "ref:" + FullName : FullName;

    public bool Equals(SchemaName other)
        => IsReference == other.IsReference && String.Equals(FullName, other.FullName, StringComparison.OrdinalIgnoreCase);
    public bool Equals(string? other) => String.Equals(ToString(), other, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj switch
    {
        SchemaName name => Equals(name),
        string text => Equals(text),
        _ => false
    };
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.OrdinalIgnoreCase);

    public static bool operator ==(SchemaName left, SchemaName right) => left.Equals(right);
    public static bool operator !=(SchemaName left, SchemaName right) => !left.Equals(right);

    public static implicit operator SchemaName(string value) => new(value);
    public static implicit operator string(SchemaName name) => name.ToString();
}
