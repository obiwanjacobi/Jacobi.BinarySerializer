namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// A contiguous range of fields (in plan order) to write or read. Fields outside the range are not represented on the wire.
/// The groups that lead to the range are still entered and exited, so all group logic (layout Begin/End, repeats, ...) applies.
/// Create one with <see cref="ExecutionPlan.CreateRange"/>.
/// </summary>
public sealed class PlanRange
{
    private readonly HashSet<NodeInfo> _included = new(ReferenceEqualityComparer.Instance);

    internal PlanRange(NodeInfo from, NodeInfo to)
    {
        First = FirstField(from);
        Last = LastField(to);

        var fields = new List<FieldInfo>();
        CollectFields(Root(from), fields);

        var start = fields.IndexOf(First);
        var end = fields.IndexOf(Last);
        if (start > end)
        {
            throw new ArgumentException($"The range start '{from.Path}' comes after the range end '{to.Path}' in the plan.");
        }

        for (var i = start; i <= end; i++)
        {
            // the field and all groups on the way up to the root
            for (NodeInfo? node = fields[i]; node is not null; node = node.Parent)
            {
                _included.Add(node);
            }
        }
    }

    /// <summary>The first field of the range.</summary>
    public FieldInfo First { get; }

    /// <summary>The last field of the range.</summary>
    public FieldInfo Last { get; }

    /// <summary>True when the node is a field in the range, or a group that contains one.</summary>
    internal bool Includes(NodeInfo node) => _included.Contains(node);

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
