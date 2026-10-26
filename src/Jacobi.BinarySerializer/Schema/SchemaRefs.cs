using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>A constant, a reference to a field value ('ref:') or a reference to a published value ('pub:').</summary>
public union SchemaValueOrRef<T>(SchemaNodeRef, SchemaPubRef, T) { }

/// <summary>
/// The instance of a repeat that a <see cref="SchemaNodeRef"/> targets: 'ref:Root.Items[2].Length' (the third item)
/// or 'ref:Root.Items[].Length' (the same item as the referring node).
/// </summary>
/// <param name="Node">The schema path of the repeat the index applies to (without indices): 'Root.Items'.</param>
/// <param name="Index">The zero-based item index, or <see cref="Current"/>.</param>
public readonly record struct SchemaInstanceIndex(string Node, int Index)
{
    /// <summary>The index of 'the same instance as the referring node' ('[]').</summary>
    public const int Current = -1;

    public bool IsCurrent => Index == Current;
}

/// <summary>
/// A reference to a node (field) in the schema by its schema path: 'ref:Root.Header.Length'.
/// A path that starts with one or more '^' segments is relative to the referring node: each '^' goes up one level,
/// so 'ref:^.Sibling' is a sibling of the referrer and 'ref:^.^.Group.Node' goes up two levels and then down.
/// A repeat on the path may carry an instance index ('[n]' or '[]'); without one the first item is meant.
/// Validated when the execution plan is built.
/// </summary>
public sealed class SchemaNodeRef
{
    public const string Prefix = "ref:";

    /// <summary>The path segment that goes up one level from the referring node.</summary>
    public const string Up = "^";

    /// <summary>The schema path without instance indices.</summary>
    public required string Path { get; init; }

    public IReadOnlyList<SchemaInstanceIndex> Indices { get; init; } = [];

    /// <summary>True when the path is relative to the referring node (starts with '^').</summary>
    public bool IsRelative => Path == Up || Path.StartsWith(Up + SchemaPath.Separator, StringComparison.Ordinal);

    /// <summary>
    /// Resolves a relative reference against the path of the referring node (an absolute reference is returned as is).
    /// Returns null when the path goes up beyond the root.
    /// </summary>
    public SchemaNodeRef? Resolve(SchemaPath referrer)
    {
        if (!IsRelative)
        {
            return this;
        }

        var path = ResolvePath(Path, referrer);
        if (path is null)
        {
            return null;
        }

        var indices = Indices.Select(index => index with { Node = ResolvePath(index.Node, referrer)! }).ToArray();
        return new SchemaNodeRef { Path = path, Indices = indices };
    }

    private static string? ResolvePath(string relative, SchemaPath referrer)
    {
        var current = referrer;
        var segments = relative.Split(SchemaPath.Separator);
        var i = 0;
        for (; i < segments.Length && segments[i] == Up; i++)
        {
            current = current.Parent;
            if (current.IsEmpty)
            {
                return null;
            }
        }

        for (; i < segments.Length; i++)
        {
            current = current.Append(segments[i]);
        }

        return current.Value;
    }

    public override string ToString()
    {
        if (Indices.Count == 0)
        {
            return Prefix + Path;
        }

        var builder = new StringBuilder(Prefix);
        var walked = String.Empty;
        foreach (var segment in Path.Split(SchemaPath.Separator))
        {
            if (walked.Length > 0)
            {
                builder.Append(SchemaPath.Separator);
                walked += SchemaPath.Separator;
            }

            walked += segment;
            builder.Append(segment);
            foreach (var index in Indices)
            {
                if (index.Node == walked)
                {
                    builder.Append(index.IsCurrent ? "[]" : $"[{index.Index.ToString(CultureInfo.InvariantCulture)}]");
                    break;
                }
            }
        }

        return builder.ToString();
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out SchemaNodeRef? value)
    {
        value = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length <= Prefix.Length)
        {
            return false;
        }

        var payload = text[Prefix.Length..];
        if (!IsValidUp(payload))
        {
            return false;
        }

        if (payload.IndexOf('[') < 0 && payload.IndexOf(']') < 0)
        {
            value = new SchemaNodeRef { Path = payload };
            return true;
        }

        var path = new StringBuilder();
        var indices = new List<SchemaInstanceIndex>();
        foreach (var segment in payload.Split(SchemaPath.Separator))
        {
            var name = segment;
            var index = -2;
            var open = segment.IndexOf('[');
            if (open >= 0)
            {
                if (open == 0 || !segment.EndsWith(']'))
                {
                    return false;
                }

                name = segment[..open];
                var inner = segment[(open + 1)..^1];
                if (inner.Length == 0)
                {
                    index = SchemaInstanceIndex.Current;
                }
                else if (!Int32.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out index))
                {
                    return false;
                }
            }

            if (name.Contains('[') || name.Contains(']'))
            {
                return false;
            }

            if (path.Length > 0)
            {
                path.Append(SchemaPath.Separator);
            }

            path.Append(name);
            if (index != -2)
            {
                indices.Add(new SchemaInstanceIndex(path.ToString(), index));
            }
        }

        value = new SchemaNodeRef { Path = path.ToString(), Indices = indices };
        return true;
    }

    // '<' segments may only lead the path, without an index, and must be followed by at least one node name.
    private static bool IsValidUp(string payload)
    {
        var segments = payload.Split(SchemaPath.Separator);
        var i = 0;
        while (i < segments.Length && segments[i] == Up)
        {
            i++;
        }

        if (i == segments.Length)
        {
            return false;
        }

        for (; i < segments.Length; i++)
        {
            if (segments[i].StartsWith(Up, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A reference to a value that a processor publishes at runtime: 'pub:namespace.name'
/// (for example 'pub:sys.varint.length'). Not validated at build time.
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
