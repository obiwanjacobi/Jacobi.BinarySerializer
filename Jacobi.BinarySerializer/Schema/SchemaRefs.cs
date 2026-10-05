using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// The instance of a repeat that a <see cref="SchemaNodeRef"/> targets: 'ref:Root.Items[2].Length' (the third item)
/// or 'ref:Root.Items[.].Length' (the same item as the referring node).
/// </summary>
/// <param name="Node">The schema path of the repeat the index applies to (without indices): 'Root.Items'.</param>
/// <param name="Index">The zero-based item index, or <see cref="Current"/>.</param>
public readonly record struct SchemaInstanceIndex(string Node, int Index)
{
    /// <summary>The index of 'the same instance as the referring node' ('[.]').</summary>
    public const int Current = -1;

    public bool IsCurrent => Index == Current;
}

/// <summary>
/// A reference to a node (field) in the schema by its schema path: 'ref:Root.Header.Length'.
/// A repeat on the path may carry an instance index ('[n]' or '[.]'); without one the first item is meant.
/// Validated when the execution plan is built.
/// </summary>
public sealed class SchemaNodeRef
{
    public const string Prefix = "ref:";

    /// <summary>The schema path without instance indices.</summary>
    public required string Path { get; init; }

    public IReadOnlyList<SchemaInstanceIndex> Indices { get; init; } = [];

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
                    builder.Append(index.IsCurrent ? "[.]" : $"[{index.Index.ToString(CultureInfo.InvariantCulture)}]");
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
        if (payload.IndexOf('[') < 0 && payload.IndexOf(']') < 0)
        {
            value = new SchemaNodeRef { Path = payload };
            return true;
        }

        var path = new StringBuilder();
        var indices = new List<SchemaInstanceIndex>();
        foreach (var segment in SplitSegments(payload))
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
                if (inner == ".")
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

    /// <summary>Splits at the path separators that are not inside an index: 'a.b[.].c' gives 'a', 'b[.]', 'c'.</summary>
    private static List<string> SplitSegments(string payload)
    {
        var segments = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            switch (payload[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case SchemaPath.Separator when depth <= 0:
                    segments.Add(payload[start..i]);
                    start = i + 1;
                    break;
            }
        }

        segments.Add(payload[start..]);
        return segments;
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
