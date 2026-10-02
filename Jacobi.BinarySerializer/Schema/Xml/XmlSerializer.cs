using System.Text;
using System.Xml;

namespace Jacobi.BinarySerializer.Schema.Xml;

internal static class XmlSerializer
{
    public static SchemaDocument Deserialize(string xml)
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(XmlSchema));

        using var stringReader = new StringReader(xml);
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

        return stringWriter.ToString();
    }
}
