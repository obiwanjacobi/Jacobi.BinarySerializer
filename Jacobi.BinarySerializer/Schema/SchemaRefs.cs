using System.Diagnostics.CodeAnalysis;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// A reference to a node (field) in the schema by its schema path: 'ref:Root.Header.Length'.
/// Validated when the execution plan is built.
/// </summary>
public sealed class SchemaNodeRef
{
    public const string Prefix = "ref:";

    public required string Path { get; init; }

    public override string ToString() => Prefix + Path;

    public static bool TryParse(string? text, [NotNullWhen(true)] out SchemaNodeRef? value)
    {
        if (text is not null && text.StartsWith(Prefix, StringComparison.Ordinal) && text.Length > Prefix.Length)
        {
            value = new SchemaNodeRef { Path = text[Prefix.Length..] };
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// A reference to a value that a processor publishes at runtime: 'pub:namespace.name'
/// (for example 'pub:sys:varint.length'). Not validated at build time.
/// </summary>
public sealed class SchemaPubRef
{
    public const string Prefix = "pub:";
    public const char Separator = '.';

    public required string Namespace { get; init; }
    public required string Name { get; init; }

    public override string ToString() => $"{Prefix}{Namespace}{Separator}{Name}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out SchemaPubRef? value)
    {
        value = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = text[Prefix.Length..];
        var i = payload.LastIndexOf(Separator);
        if (i <= 0 || i == payload.Length - 1)
        {
            return false;
        }

        value = new SchemaPubRef { Namespace = payload[..i], Name = payload[(i + 1)..] };
        return true;
    }
}
