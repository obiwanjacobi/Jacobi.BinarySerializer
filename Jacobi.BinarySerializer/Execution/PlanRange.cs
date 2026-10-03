namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// A contiguous range of fields (in plan order) to write or read. Fields outside the range are not represented on the wire.
/// The groups that lead to the range are still entered and exited, so all group logic (layout Begin/End, repeats, ...) applies.
/// Create one with <see cref="ExecutionPlan.CreateRange"/>.
/// </summary>
public sealed class PlanRange
{
    // document-order position keys: per ancestor its index and, for a repeat above the node, the item index.
    private readonly int[] _from;
    private readonly int[] _to;

    internal PlanRange(NodeInfo from, NodeInfo to)
        : this(from, InstancePath.Empty, to, InstancePath.Empty)
    { }

    internal PlanRange(NodeInfo from, InstancePath fromInstance, NodeInfo to, InstancePath toInstance)
    {
        First = FirstField(from);
        Last = LastField(to);
        FromInstance = fromInstance;
        ToInstance = toInstance;

        var fields = new List<FieldInfo>();
        CollectFields(Root(from), fields);

        var start = fields.IndexOf(First);
        var end = fields.IndexOf(Last);
        if (start > end)
        {
            throw new ArgumentException($"The range start '{from.Path}' comes after the range end '{to.Path}' in the plan.");
        }

        CheckInstance(First, fromInstance);
        CheckInstance(Last, toInstance);
        _from = Key(First, fromInstance, 0);
        _to = Key(Last, toInstance, int.MaxValue);
        if (Compare(_from, _to) > 0)
        {
            throw new ArgumentException($"The range start '{from.Path}{fromInstance}' comes after the range end '{to.Path}{toInstance}'.");
        }
    }

    /// <summary>The first field of the range.</summary>
    public FieldInfo First { get; }

    /// <summary>The last field of the range.</summary>
    public FieldInfo Last { get; }

    /// <summary>The item indices of the repeats around <see cref="First"/> (empty: from the first item).</summary>
    public InstancePath FromInstance { get; }

    /// <summary>The item indices of the repeats around <see cref="Last"/> (empty: up to the last item).</summary>
    public InstancePath ToInstance { get; }

    /// <summary>
    /// True when the node (at the given instance) is a field in the range, or a group, repeat or repeat item that contains one.
    /// <paramref name="instance"/> holds the item index of every repeat above the node; for a repeat node it may hold one more: the item.
    /// </summary>
    internal bool Includes(NodeInfo node, InstancePath instance)
    {
        var key = Key(node, instance, 0);
        return Compare(key, _from) >= 0 && Compare(key, _to) <= 0;
    }

    private static void CheckInstance(FieldInfo field, InstancePath instance)
    {
        var repeats = 0;
        for (NodeInfo? node = field.Parent; node is not null; node = node.Parent)
        {
            if (node is RepeatInfo)
            {
                repeats++;
            }
        }
        if (instance.Length > repeats)
        {
            throw new ArgumentException($"'{field.Path}' is inside {repeats} repeat(s) but the instance path {instance} has {instance.Length} indices.");
        }
    }

    private static int[] Key(NodeInfo node, InstancePath instance, int fill)
    {
        var chain = new List<NodeInfo>();
        for (NodeInfo? n = node; n is not null; n = n.Parent)
        {
            chain.Add(n);
        }
        chain.Reverse();

        var key = new List<int>();
        var depth = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            key.Add(chain[i].Index);
            if (chain[i] is RepeatInfo)
            {
                if (i < chain.Count - 1)
                {
                    key.Add(depth < instance.Length ? instance[depth] : fill);
                }
                else if (depth < instance.Length)
                {
                    key.Add(instance[depth]);
                }
                depth++;
            }
        }
        return [.. key];
    }

    // compares over the shared length: a node's key is a prefix of the keys of the fields below it.
    private static int Compare(int[] key, int[] bound)
    {
        var length = Math.Min(key.Length, bound.Length);
        for (var i = 0; i < length; i++)
        {
            var c = key[i].CompareTo(bound[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    private static NodeInfo Root(NodeInfo node)
    {
        while (node.Parent is not null)
        {
            node = node.Parent;
        }
        return node;
    }

    private static void CollectFields(NodeInfo node, List<FieldInfo> fields)
    {
        switch (node)
        {
            case FieldInfo field:
                fields.Add(field);
                break;
            case GroupInfo group:
                foreach (var child in group.Children)
                {
                    CollectFields(child, fields);
                }
                break;
        }
    }

    private static FieldInfo FirstField(NodeInfo node)
        => Edge(node, first: true);

    private static FieldInfo LastField(NodeInfo node)
        => Edge(node, first: false);

    private static FieldInfo Edge(NodeInfo node, bool first)
    {
        var fields = new List<FieldInfo>();
        CollectFields(node, fields);
        if (fields.Count == 0)
        {
            throw new ArgumentException($"'{node.Path}' does not contain any fields.");
        }
        return first ? fields[0] : fields[^1];
    }
}
