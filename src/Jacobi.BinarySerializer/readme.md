# Binary Serializer

## API

How to setup and interface with the binary serializer.

```csharp
// load the schema definitions of the binary formats you wish to serialize.
SchemaSet schemas = new();
schemas.LoadFile(".json|.xml|.yml|.yaml");
schemas.LoadFromAssembly(Asembly|"*.dll|*.exe");
...
schemas.Compile(); // resolve references, validate

// load the processors (code) that perform transformation and other logic.
ProcessorManager processors = new();
processors.LoadFromAssembly(Assembly|"*.dll|*.exe");
processors.Register(IProcessorFactory);

// serialize will ask you for logical values
IValueSource valueSource = ...
// deserialize will give you logical values
IValueSink valueSink = ...

var outputStream = byte[]|Stream|IBinaryWriter;
var inputStream = byte[]|Stream|SequenceReader<byte>;

// data types: a per-serializer registry (built-in 'sys.*' types by default); register custom types.
DataTypeRegistry dataTypes = DataTypeRegistry.CreateDefault();
dataTypes.Register(new DataTypeDescriptor("my.point", typeof(Point), parser));

// available to processors
IServiceProvider services = ...;

// bring it together in the serializer (immutable, thread-safe, owns the ExecutionPlan cache)
Serializer serializer = new SerializerBuilder()
    .AddSchemas(schemas)
    .AddProcessors(processors) // or .AddServices(services)
    .AddServices(services)
    .AddDataTypes(dataTypes) // optional
    .Build();
ExecutionPlan plan = serializer.GetPlan("schemaName"); // cached

// write logical to binary
var result = serializer.Serialize(plan, IValueSource|IFieldSource, outputStream, services);
// result == NeedMoreSpace?

// read binary to logical
var result = serializer.Deserialize(plan, IValueSink|IFieldSink, inputStream, services);
// result == NeedMoreData?
```
