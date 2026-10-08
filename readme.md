# Binary Serializer

Welcome to the Schema-driven Binary Serializer project.

The idea of this project is to have an extensible framework / mechanism that can be made to (de)serialize any binary format by defining a schema for said format, similar to json-schema or xml-schema.

The schema holds structure only. Transformations such as varints, alignment, scaling, enums, compression or framing are done by pluggable **processors**.

## How it works

```
schema (JSON/XML/YML) -> SchemaSet.Compile -> ExecutionPlan -> engine (reader/writer session)
                                                              |-> processors -> codecs
```

- **Schema**: describes fields, groups, repeats and choices, and which processors apply.
- **Processors**: stateless code that transforms values, encodes fields, lays out bytes or wraps the whole stream. Built-in ones use the `sys:` prefix.
- **Values**: the engine exchanges logical values with your code through `IValueSource` (write) and `IValueSink` (read).

## Example

A schema for a simple message: a magic number, a count and a list of 16-bit values.

```json
{
  "name": "Sample",
  "members": [
    {
      "name": "Message",
      "kind": "group",
      "members": [
        { "name": "Magic", "kind": "field", "type": "UInt32", "value": 3405691582 },
        { "name": "Count", "kind": "field", "type": "UInt8" },
        {
          "name": "Items",
          "kind": "repeat",
          "count": { "ref": "Message.Count" },
          "members": [
            { "name": "Value", "kind": "field", "type": "UInt16" }
          ]
        }
      ]
    }
  ]
}
```

Using it:

```csharp
SchemaSet schemas = new();
schemas.LoadFile("sample.json");
schemas.Compile();

Serializer serializer = new SerializerBuilder()
    .AddSchemas(schemas)
    .Build();

ExecutionPlan plan = serializer.GetPlan("Message");

// write logical values to binary
serializer.Serialize(plan, valueSource, outputStream, services);

// read binary into logical values
serializer.Deserialize(plan, valueSink, inputStream, services);
```

See the [API readme](src/Jacobi.BinarySerializer/readme.md) for the complete setup (processors, data types, services).

## Documentation

| Topic | Readme |
|-------|--------|
| Core library and public API | [Jacobi.BinarySerializer](src/Jacobi.BinarySerializer/readme.md) |
| Schema format | [Schema](src/Jacobi.BinarySerializer/Schema/readme.md) |
| Execution engine | [Execution](src/Jacobi.BinarySerializer/Execution/readme.md) |
| Processor model | [Processor](src/Jacobi.BinarySerializer/Processor/readme.md) |
| Built-in `sys:` processors | [Processors](src/Jacobi.BinarySerializer.Processors/readme.md) |
| Real-world example (PNG) | [Png](src/Jacobi.BinarySerializer.IntegrationTests/Png/readme.md) |

> The project is under development; see the TODO lists in the readmes above.

