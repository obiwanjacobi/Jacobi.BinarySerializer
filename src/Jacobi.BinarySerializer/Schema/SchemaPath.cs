namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// Identifies a node in the execution plan by the names from the root down, separated by '.' (e.g. 'Root.Header.Length').
/// </summary>
public readonly struct SchemaPath : IEquatable<SchemaPath>, IEquatable<string>
{
    public const char Separator = '.';

    private readonly string? _value;

    public SchemaPath(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>The name of the node (the last segment).</summary>
    public string Name
    {
        get
        {
            var index = Value.LastIndexOf(Separator);
            return index < 0 ? Value : Value[(index + 1)..];
        }
    }

    /// <summary>The path of the parent node; empty for a root path.</summary>
    public SchemaPath Parent
    {
        get
        {
            var index = Value.LastIndexOf(Separator);
            return index < 0 ? default : new SchemaPath(Value[..index]);
        }
    }

    public IEnumerable<string> Segments => IsEmpty ? [] : Value.Split(Separator);

    public SchemaPath Append(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return IsEmpty ? new SchemaPath(name) : new SchemaPath(Value + Separator + name);
    }

    /// <summary>True when this path equals <paramref name="other"/> or is nested below it.</summary>
    public bool IsSameOrDescendantOf(SchemaPath other)
        => Value == other.Value
           || (Value.Length > other.Value.Length
               && Value.StartsWith(other.Value, StringComparison.Ordinal)
               && Value[other.Value.Length] == Separator);

    public bool Equals(SchemaPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public bool Equals(string? other) => string.Equals(Value, other, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj switch
    {
        SchemaPath path => Equals(path),
        string text => Equals(text),
        _ => false
    };
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => Value;

    public static bool operator ==(SchemaPath left, SchemaPath right) => left.Equals(right);
    public static bool operator !=(SchemaPath left, SchemaPath right) => !left.Equals(right);

    public static implicit operator SchemaPath(string value) => new(value);
    public static implicit operator string(SchemaPath path) => path.Value;
}
