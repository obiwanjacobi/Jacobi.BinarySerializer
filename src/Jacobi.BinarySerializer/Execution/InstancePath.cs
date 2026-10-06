using System.Text;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// The instance indices of the repeats that lead to a node, ordered outermost to innermost.
/// A <see cref="Schema.SchemaPath"/> is shared by all items of a repeat; the instance path tells the iterations apart.
/// Empty means the node is not inside a repeat.
/// </summary>
public readonly struct InstancePath : IEquatable<InstancePath>
{
    private readonly int[]? _indices;

    public InstancePath(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var array = indices.ToArray();
        _indices = array.Length == 0 ? null : array;
    }

    public static InstancePath Empty => default;

    /// <summary>The number of repeats on the way to the node.</summary>
    public int Length => _indices?.Length ?? 0;

    public bool IsEmpty => Length == 0;

    public int this[int depth] => _indices is not null
        ? _indices[depth]
        : throw new ArgumentOutOfRangeException(nameof(depth));

    /// <summary>The index of the innermost repeat; -1 when empty.</summary>
    public int Last => _indices is { Length: > 0 } ? _indices[^1] : -1;

    public ReadOnlySpan<int> AsSpan() => _indices;

    /// <summary>Returns a new path with the index of a (nested) repeat item added.</summary>
    public InstancePath Append(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var length = Length;
        var array = new int[length + 1];
        _indices?.CopyTo(array, 0);
        array[length] = index;
        return new InstancePath(array);
    }

    /// <summary>In a reference template: 'the same instance as the referring node' at that depth.</summary>
    public const int Current = -1;

    /// <summary>True when the path holds <see cref="Current"/> markers (a reference template, not an actual instance).</summary>
    public bool IsRelative
    {
        get
        {
            foreach (var index in AsSpan())
            {
                if (index < 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Replaces each <see cref="Current"/> marker with the index at the same depth in <paramref name="current"/>.</summary>
    public InstancePath ResolveRelative(InstancePath current)
    {
        if (!IsRelative)
        {
            return this;
        }

        var source = AsSpan();
        var array = new int[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] >= 0)
            {
                array[i] = source[i];
            }
            else if (i < current.Length)
            {
                array[i] = current[i];
            }
            else
            {
                throw new InvalidOperationException($"The instance {current} has no index at depth {i} to resolve the relative index.");
            }
        }
        return new InstancePath(array);
    }

    public bool Equals(InstancePath other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is InstancePath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var index in AsSpan())
        {
            hash.Add(index);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(InstancePath left, InstancePath right) => left.Equals(right);
    public static bool operator !=(InstancePath left, InstancePath right) => !left.Equals(right);

    /// <summary>Formats as <c>[2][0]</c>; empty for no repeats.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var index in AsSpan())
        {
            sb.Append('[').Append(index).Append(']');
        }
        return sb.ToString();
    }
}
