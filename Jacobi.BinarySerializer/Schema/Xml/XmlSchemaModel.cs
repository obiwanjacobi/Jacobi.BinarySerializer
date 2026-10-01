using System.Xml;
using System.Xml.Serialization;

namespace Jacobi.BinarySerializer.Schema.Xml;

[XmlRoot("schema")]
public sealed class XmlSchema
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlArray("children")]
    [XmlArrayItem("field", typeof(XmlSchemaFieldNode))]
    [XmlArrayItem("group", typeof(XmlSchemaGroupNode))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeatNode))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoiceNode))]
    public List<XmlSchemaNode> Children { get; set; } = [];

    [XmlArray("typeDefs")]
    [XmlArrayItem("field", typeof(XmlSchemaFieldNode))]
    [XmlArrayItem("group", typeof(XmlSchemaGroupNode))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeatNode))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoiceNode))]
    public List<XmlSchemaNode> TypeDefs { get; set; } = [];

    [XmlArray("codecDefs")]
    [XmlArrayItem("codec")]
    public List<XmlSchemaCodecRef> CodecDefs { get; set; } = [];

    [XmlArray("includes")]
    [XmlArrayItem("include")]
    public List<XmlSchemaDocumentRef> Includes { get; set; } = [];

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];

    [XmlAnyAttribute]
    public XmlAttribute[]? AdditionalAttributes { get; set; }

    [XmlAnyElement]
    public XmlElement[]? AdditionalElements { get; set; }
}

public abstract class XmlSchemaNode
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];

    [XmlAnyAttribute]
    public XmlAttribute[]? AdditionalAttributes { get; set; }

    [XmlAnyElement]
    public XmlElement[]? AdditionalElements { get; set; }
}

public sealed class XmlSchemaFieldNode : XmlSchemaNode
{
    [XmlElement("codec")]
    public XmlSchemaCodecRef Codec { get; set; } = new() { Codec = string.Empty };

    [XmlAttribute("type")]
    public SchemaDataType Type { get; set; }
}

public class XmlSchemaGroupNode : XmlSchemaNode
{
    [XmlArray("pipeline")]
    [XmlArrayItem("codec")]
    public List<XmlSchemaCodecRef> Pipeline { get; set; } = [];

    [XmlArray("children")]
    [XmlArrayItem("field", typeof(XmlSchemaFieldNode))]
    [XmlArrayItem("group", typeof(XmlSchemaGroupNode))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeatNode))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoiceNode))]
    public List<XmlSchemaNode> Children { get; set; } = [];
}

public sealed class XmlSchemaCodecRef
{
    [XmlAttribute("codec")]
    public string Codec { get; set; } = string.Empty;

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];
}

public sealed class XmlSchemaDocumentRef
{
    [XmlAttribute("schema")]
    public string Schema { get; set; } = string.Empty;

    [XmlAttribute("path")]
    public string? Path { get; set; }
}

public sealed class XmlSchemaRepeatNode : XmlSchemaGroupNode
{
    [XmlAttribute("count")]
    public string? Count { get; set; }
}

public sealed class XmlSchemaChoiceNode : XmlSchemaGroupNode
{
    [XmlAttribute("selectedIndex")]
    public string? SelectedIndex { get; set; }
}

public sealed class XmlSchemaProperty
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("type")]
    public string? Type { get; set; }

    [XmlAttribute("value")]
    public string? Value { get; set; }

    [XmlText]
    public string? Text { get; set; }
}
