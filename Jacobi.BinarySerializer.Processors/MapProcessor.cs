using System.Globalization;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class MapProcessor : IValueProcessor
{
    private const string LogicalProperty = "logical";

    // each property maps a logical value (name) to a physical value (value), parsed as the field type.
    // a property without a value (null) is the default: on read, physical values that are not mapped give its logical value.
    // the default has no physical value, so it cannot be written.
    // the optional 'logical' property sets the data type of the logical values (default String).

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (logicalType, physicalType, entries) = GetMap(context);
        var logical = Normalize(logicalValue.Value, logicalType, context);
        var index = entries.FindIndex(e => Equals(e.Logical, logical));
        if (index < 0)
        {
            throw context.Logger.Fail($"Value '{logicalValue.Value}' is not mapped.");
        }
        var entry = entries[index];
        if (entry.Physical is null)
        {
            throw context.Logger.Fail($"Value '{logicalValue.Value}' is the default of the map and has no physical value to write.");
        }

        return new(logicalValue.Name, DataTypeCodec.ClrType(physicalType) ?? typeof(string), entry.Physical);
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (logicalType, physicalType, entries) = GetMap(context);
        var physical = Normalize(logicalValue.Value, physicalType, context);
        var index = entries.FindIndex(e => e.Physical is not null && Equals(e.Physical, physical));
        if (index < 0)
        {
            index = entries.FindIndex(e => e.Physical is null);
        }
        if (index < 0)
        {
            throw context.Logger.Fail($"Value '{logicalValue.Value}' is not mapped.");
        }
        var entry = entries[index];

        return new(logicalValue.Name, DataTypeCodec.ClrType(logicalType) ?? typeof(string), entry.Logical);
    }

    private static (SchemaDataType Logical, SchemaDataType Physical, List<(object Logical, object? Physical)> Entries) GetMap(ValueProcessorContext context)
    {
        var physicalType = context.DataType
            ?? throw context.Logger.Fail("The map processor requires a field (or a reference to a field) to know the physical type.");

        var logicalType = SchemaDataType.String;
        if (context.Properties.Find(LogicalProperty) is { } logicalProperty
            && !Enum.TryParse(logicalProperty.Value, true, out logicalType))
        {
            throw context.Logger.Fail($"Invalid logical type '{logicalProperty.Value}'.");
        }

        var entries = new List<(object, object?)>();
        foreach (var property in context.Properties.ShortNames())
        {
            if (property.Key.Equals(LogicalProperty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!DataTypeCodec.TryParse(logicalType, property.Key, out var logical) || logical is null)
            {
                throw context.Logger.Fail($"The map key '{property.Key}' is not a valid {logicalType} value.");
            }
            if (property.Value is null)
            {
                entries.Add((logical, null));
                continue;
            }
            if (!DataTypeCodec.TryParse(physicalType, property.Value, out var physical) || physical is null)
            {
                throw context.Logger.Fail($"The map value '{property.Value}' is not a valid {physicalType} value.");
            }

            entries.Add((logical, physical));
        }

        if (entries.Count == 0)
        {
            throw context.Logger.Fail("The map processor requires at least one mapping.");
        }

        return (logicalType, physicalType, entries);
    }

    private static object Normalize(object value, SchemaDataType type, ValueProcessorContext context)
    {
        var clrType = DataTypeCodec.ClrType(type) ?? typeof(string);
        try
        {
            return Convert.ChangeType(value, clrType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw context.Logger.Fail($"Value '{value}' is not a valid {type} value.", ex);
        }
    }

    public ProcessorKey Key => new("sys.map");
    public string Name => "Map Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [new(LogicalProperty, typeof(string), false)];
}
