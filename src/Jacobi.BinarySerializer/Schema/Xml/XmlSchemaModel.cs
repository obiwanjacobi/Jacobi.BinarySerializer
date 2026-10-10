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
    [XmlArrayItem("typeDef")]
    public List<XmlSchemaTypeDef> TypeDefs { get; set; } = [];

    [XmlArray("dataTypeDefs")]
    [XmlArrayItem("dataTypeDef")]
    public List<XmlSchemaDataTypeDef> DataTypeDefs { get; set; } = [];

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

/// <summary>
/// A data type defined in the schema: a base data type with facets and processors.
/// </summary>
public sealed class XmlSchemaDataTypeDef
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("basedOn")]
    public string BasedOn { get; set; } = string.Empty;

    [XmlAttribute("min")]
    public string? Min { get; set; }

    [XmlAttribute("max")]
    public string? Max { get; set; }

    [XmlAttribute("scale")]
    public string? Scale { get; set; }

    [XmlAttribute("shift")]
    public string? Shift { get; set; }

    [XmlArray("options")]
    [XmlArrayItem("option")]
    public List<XmlSchemaOption>? Options { get; set; }

    [XmlArray("processors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> Processors { get; set; } = [];

    [XmlArray("properties")]
    [XmlArrayItem("property")]
    public List<XmlSchemaProperty> Properties { get; set; } = [];

    [XmlAnyAttribute]
    public XmlAttribute[]? AdditionalAttributes { get; set; }

    [XmlAnyElement]
    public XmlElement[]? AdditionalElements { get; set; }
}

public sealed class XmlSchemaOption
{
    [XmlAttribute("value")]
    public string Value { get; set; } = string.Empty;

    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A reusable type: an optional data type plus processors.
/// </summary>
public sealed class XmlSchemaTypeDef
{
    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("datatype")]
    public string? DataType { get; set; }

    [XmlArray("processors")]
    [XmlArrayItem("processor")]
    public List<XmlSchemaProcessorRef> Processors { get; set; } = [];

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

    /// <summary>
    /// Optional reference to a typeDef that defines the type and processors of the node.
    /// </summary>
    [XmlAttribute("typeDef")]
    public string? TypeDef { get; set; }

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

    [XmlAttribute("datatype")]
    public string? DataType { get; set; }

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

    /// <summary>A signed offset in bytes (attribute) relative to the current position where the field is read from (virtual field).</summary>
    [XmlAttribute("byteOffset")]
    public string? ByteOffset { get; set; }
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
    [XmlAttribute("name")]
    public string Processor { get; set; } = string.Empty;

    [XmlAttribute("pubns")]
    public string? PubNs { get; set; }

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
