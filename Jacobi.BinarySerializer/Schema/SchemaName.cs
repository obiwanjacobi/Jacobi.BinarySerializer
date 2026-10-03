namespace Jacobi.BinarySerializer.Schema;

public readonly struct SchemaName : IEquatable<SchemaName>, IEquatable<string>
{
    public const char Separator = '.';

    public SchemaName()
        => throw new InvalidOperationException("SchemaName must be initialized with a name.");

    public SchemaName(string moniker)
    {
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

    override public string ToString()
        => FullName;

    public bool Equals(SchemaName other) => string.Equals(FullName, other.FullName, StringComparison.Ordinal);
    public bool Equals(string? other) => string.Equals(FullName, other, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj switch
    {
        SchemaPath path => Equals(path),
        string text => Equals(text),
        _ => false
    };
    public override int GetHashCode() => FullName.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(SchemaName left, SchemaName right) => left.Equals(right);
    public static bool operator !=(SchemaName left, SchemaName right) => !left.Equals(right);

    public static implicit operator SchemaName(string value) => new(value);
    public static implicit operator string(SchemaName path) => path.FullName;
}
