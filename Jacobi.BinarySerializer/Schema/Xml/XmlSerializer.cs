using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jacobi.BinarySerializer.Schema.Xml;

internal static class XmlSerializer
{
    public static SchemaDocument Deserialize(string xml)
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(XmlSchema));

        using var stringReader = new StringReader(NilToAttribute(xml));
        var xmlSchema = serializer.Deserialize(stringReader) as XmlSchema
            ?? throw new InvalidOperationException("Failed to deserialize XML schema.");

        var schema = XmlSchemaMapper.ToSchema(xmlSchema);

        return SchemaDocumentMapper.ToDocument(schema);
    }

    public static string Serialize(SchemaDocument document)
    {
        var xmlSchema = XmlSchemaMapper.FromSchema(document);

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(XmlSchema));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = Encoding.UTF8
        };

        using var stringWriter = new StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            serializer.Serialize(writer, xmlSchema);
        }

        return AttributeToNil(stringWriter.ToString());
    }

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // the framework serializer turns an xsi:nil element into a null list item (losing its name),
    // so xsi:nil on a property is exchanged for a plain 'nil' attribute while (de)serializing.
    private static string NilToAttribute(string xml)
    {
        var document = XDocument.Parse(xml);
        foreach (var property in document.Descendants().Where(e => e.Name.LocalName == "property").ToList())
        {
            var nil = property.Attribute(Xsi + "nil");
            if (nil is null)
            {
                continue;
            }
            nil.Remove();
            if (XmlConvert.ToBoolean(nil.Value))
            {
                property.SetAttributeValue("nil", "true");
            }
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string AttributeToNil(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root!;
        var changed = false;
        foreach (var property in document.Descendants().Where(e => e.Name.LocalName == "property" && e.Attribute("nil") is not null).ToList())
        {
            property.Attribute("nil")!.Remove();
            property.SetAttributeValue(Xsi + "nil", "true");
            changed = true;
        }
        if (!changed)
        {
            return xml;
        }
        root.SetAttributeValue(XNamespace.Xmlns + "xsi", Xsi.NamespaceName);
        return document.ToString();
    }
}
