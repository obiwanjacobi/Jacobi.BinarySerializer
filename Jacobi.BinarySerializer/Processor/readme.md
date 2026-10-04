# Processor

An object that converts/translates data from one format to another. 

- [ ] define a list of global/well-known properties (names and datatypes).
- [ ] 

## Well-Known Properties

A common set of properties that are used by the mechanism or other processors. 

| Property Name | Data Type | Description |
|---------------|-----------|-------------|
| pubns | string | Public Namespace: the namespace used when a processor publishes public values. |
| length | uint | The length of the data being processed. String with a fixed length can be encoded this way. |

## Specifying Processor Properties

In general the processor key is to be used as a prefix to the property name when specifying properties for a processor.

`sys:enum.map` - where `sys:enum` is the processor key and `map` is the property name.

However when specifying properties inside the processor-definitions in a schema, the properties are alread listed under the processor key, so the prefix is not needed.

## Publishing Public Values

A processor can publish public values that can be used by other components in the pipeline.
These values can be used to configure the processor or to provide information about the processing that has been done.

A public value can be published using a namespace and a name. 
The namespace is used to group related values together and prevents collisions between multiple processors publishing the same value.
The name is used to identify the value within the namespace.

Typically the namespace can be set on the publishing processor.