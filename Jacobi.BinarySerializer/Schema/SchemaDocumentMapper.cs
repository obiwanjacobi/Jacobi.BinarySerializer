namespace Jacobi.BinarySerializer.Schema;

internal static class SchemaDocumentMapper
{
    public static SchemaDocument ToDocument(Schema schema)
    {
        var groups = new List<SchemaGroup>();
        var fields = new List<SchemaField>();

        foreach (var root in schema.Children)
        {
            if (root is SchemaGroup group)
            {
                CollectNodes(group, groups, fields);
            }
        }

        return new SchemaDocument
        {
            Name = schema.Name,
            PropertyList = schema.Properties.ToList(),
            ChildList = schema.ChildList,
            TypeDefs = schema.TypeDefs,
            ProcessorDefs = schema.ProcessorDefs,
            Includes = schema.Includes,
            Roots = schema.Children.OfType<SchemaGroup>().ToList(),
            Groups = groups,
            Fields = fields
        };
    }

    private static void CollectNodes(SchemaGroup group, ICollection<SchemaGroup> groups, ICollection<SchemaField> fields)
    {
        groups.Add(group);

        foreach (var child in group.Children)
        {
            switch (child)
            {
                case SchemaField field:
                    fields.Add(field);
                    break;
                case SchemaGroup childGroup:
                    CollectNodes(childGroup, groups, fields);
                    break;
            }
        }
    }
}
