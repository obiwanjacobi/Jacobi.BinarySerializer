using System.Xml;
using System.Xml.Serialization;

namespace Jacobi.BinarySerializer.Schema.Xml;

[XmlRoot("schema")]
public sealed class XmlSchema
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlArray("members")]
    [XmlArrayItem("field", typeof(XmlSchemaField))]
    [XmlArrayItem("group", typeof(XmlSchemaGroup))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeat))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoice))]
    public List<XmlSchemaNode> Members { get; set; } = [];

    [XmlArray("typeDefs")]
    [XmlArrayItem("field", typeof(XmlSchemaField))]
    [XmlArrayItem("group", typeof(XmlSchemaGroup))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeat))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoice))]
    public List<XmlSchemaNode> TypeDefs { get; set; } = [];

    [XmlArray("processors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> Processors { get; set; } = [];

    [XmlArray("processorDefs")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorDef> ProcessorDefs { get; set; } = [];

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

public sealed class XmlSchemaField : XmlSchemaNode
{
    [XmlElement("processor")]
    public List<XmlSchemaProcessorRef> Processors { get; set; } = [];

    [XmlAttribute("type")]
    public string? Type { get; set; }

    /// <summary>A constant value (attribute); use <see cref="ValueRef"/> to refer to a value.</summary>
    [XmlAttribute("value")]
    public string? Value { get; set; }

    [XmlElement("value")]
    public XmlSchemaValueRef? ValueRef { get; set; }

    /// <summary>A constant length in bytes (attribute); use <see cref="ByteLengthRef"/> to refer to a value.</summary>
    [XmlAttribute("byteLength")]
    public string? ByteLength { get; set; }

    [XmlElement("byteLength")]
    public XmlSchemaValueRef? ByteLengthRef { get; set; }
}

public class XmlSchemaGroup : XmlSchemaNode
{
    [XmlArray("processors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> Processors { get; set; } = [];

    [XmlArray("members")]
    [XmlArrayItem("field", typeof(XmlSchemaField))]
    [XmlArrayItem("group", typeof(XmlSchemaGroup))]
    [XmlArrayItem("repeat", typeof(XmlSchemaRepeat))]
    [XmlArrayItem("choice", typeof(XmlSchemaChoice))]
    public List<XmlSchemaNode> Members { get; set; } = [];

    /// <summary>A constant size in bytes (attribute); use <see cref="ByteSizeRef"/> to refer to a value.</summary>
    [XmlAttribute("byteSize")]
    public string? ByteSize { get; set; }

    [XmlElement("byteSize")]
    public XmlSchemaValueRef? ByteSizeRef { get; set; }
}

/// <summary>A named processor declaration: 'name' is used in a 'ref:name' (or 'ref:document.name').</summary>
public sealed class XmlSchemaProcessorDef
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("processor")]
    public string Processor { get; set; } = string.Empty;

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];

    [XmlAnyAttribute]
    public XmlAttribute[]? AdditionalAttributes { get; set; }

    [XmlAnyElement]
    public XmlElement[]? AdditionalElements { get; set; }
}

/// <summary>A processor key ('namespace.id') or a reference to a processor declaration ('ref:name').</summary>
public sealed class XmlSchemaProcessorRef
{
    [XmlAttribute("processor")]
    public string Processor { get; set; } = string.Empty;

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];

    [XmlAnyAttribute]
    public XmlAttribute[]? AdditionalAttributes { get; set; }

    [XmlAnyElement]
    public XmlElement[]? AdditionalElements { get; set; }
}

public sealed class XmlSchemaDocumentRef
{
    [XmlAttribute("schema")]
    public string Schema { get; set; } = string.Empty;

    [XmlAttribute("path")]
    public string? Path { get; set; }
}

public sealed class XmlSchemaRepeat : XmlSchemaGroup
{
    /// <summary>A constant count (attribute); use <see cref="CountRef"/> to refer to a value.</summary>
    [XmlAttribute("count")]
    public string? Count { get; set; }

    [XmlElement("count")]
    public XmlSchemaValueRef? CountRef { get; set; }

    [XmlArray("valueProcessors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> ValueProcessors { get; set; } = [];
}

public sealed class XmlSchemaChoice : XmlSchemaGroup
{
    /// <summary>A constant index (attribute); use <see cref="SelectedIndexRef"/> to refer to a value.</summary>
    [XmlAttribute("selectedIndex")]
    public string? SelectedIndex { get; set; }

    [XmlElement("selectedIndex")]
    public XmlSchemaValueRef? SelectedIndexRef { get; set; }

    [XmlArray("valueProcessors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> ValueProcessors { get; set; } = [];
}

public sealed class XmlSchemaValueRef
{
    [XmlAttribute("ref")]
    public string? Ref { get; set; }

    [XmlAttribute("pub")]
    public string? Pub { get; set; }
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

    /// <summary>A property without a value (null). In the xml document this is written as xsi:nil="true" (translated by the XmlSerializer).</summary>
    [XmlAttribute("nil")]
    [System.ComponentModel.DefaultValue(false)]
    public bool Nil { get; set; }
}
