namespace Jacobi.BinarySerializer.Schema;

internal static class SchemaDocumentMapper
{
    public static SchemaDocument ToDocument(Schema schema)
    {
        var groups = new List<SchemaGroup>();
        var fields = new List<SchemaField>();

        foreach (var root in schema.Children)
        {
            CollectNodes(root, groups, fields);
        }

        return new SchemaDocument
        {
            Name = schema.Name,
            Properties = schema.Properties,
            Children = schema.Children,
            TypeDefs = schema.TypeDefs,
            CodecDefs = schema.CodecDefs,
            Includes = schema.Includes,
            Roots = schema.Children.ToList(),
            Groups = groups,
            Fields = fields
        };
    }

    public static Schema FromDocument(SchemaDocument document)
    {
        return new Schema
        {
            Name = document.Name,
            Properties = document.Properties,
            Children = document.Roots.OfType<SchemaGroup>().ToList(),
            TypeDefs = document.TypeDefs,
            CodecDefs = document.CodecDefs,
            Includes = document.Includes
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
