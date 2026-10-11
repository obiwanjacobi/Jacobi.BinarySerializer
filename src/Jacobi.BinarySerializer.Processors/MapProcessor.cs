using System.Globalization;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class MapProcessor : ProcessorBase, IValueProcessor
{
    private static readonly PropertyDescriptor ToProperty = new("to", "sys.string", false);
    private static readonly PropertyDescriptor DefaultProperty = new("default", "sys.string", false);

    // read direction: each property maps a source value (name) to a result value (value).
    // the source value is parsed as the data type of the field (or the value type of the repeat/choice).
    // the optional 'to' property sets the data type of the result values (default String).
    // the optional 'default' property is the result for source values that are not mapped; without it an unmapped value is an error.
    // the default has no source value, so it cannot be written.

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var map = GetMap(context);
        var result = Normalize(logicalValue.Value, map.ResultType, context);
        var index = map.Entries.FindIndex(e => Equals(e.Result, result));
        if (index < 0)
        {
            throw context.Logger.Fail($"Value '{logicalValue.Value}' is not mapped.");
        }

        return new(logicalValue.Name, map.SourceType.ClrType, map.Entries[index].Source);
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var map = GetMap(context);
        var source = Normalize(logicalValue.Value, map.SourceType, context);
        var index = map.Entries.FindIndex(e => Equals(e.Source, source));
        if (index >= 0)
        {
            return new(logicalValue.Name, map.ResultType.ClrType, map.Entries[index].Result);
        }
        if (map.Default is not null)
        {
            return new(logicalValue.Name, map.ResultType.ClrType, map.Default);
        }

        throw context.Logger.Fail($"Value '{logicalValue.Value}' is not mapped and the map has no default.");
    }

    private sealed class MapState
    {
        public DataTypeDescriptor SourceType { get; set; } = null!;
        public DataTypeDescriptor ResultType { get; set; } = null!;
        public object? Default { get; set; }
        public List<(object Source, object Result)> Entries { get; } = [];
    }

    private static MapState GetMap(ValueProcessorContext context)
    {
        if (!context.GetOrCreateState<MapState>(StateScope.Binding, out var map))
        {
            ParseMap(map, context);
        }
        return map;
    }

    private static void ParseMap(MapState map, ValueProcessorContext context)
    {
        var sourceType = context.DataType
            ?? throw context.Logger.Fail("The map processor requires a field (or a reference to a field) to know the type of the source values.");

        var resultType = context.DataTypes.Get(new SchemaName("sys.string"));
        if (context.Properties.GetOrDefault<string>(ToProperty) is { } resultName
            && !context.DataTypes.TryGet(new SchemaName(resultName), out resultType!))
        {
            throw context.Logger.Fail($"Invalid result type '{resultName}'.");
        }

        object? defaultValue = null;
        var entries = new List<(object, object)>();
        foreach (var property in context.Properties.ShortNames())
        {
            if (property.Key.Equals(ToProperty.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (property.Key.Equals(DefaultProperty.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value is null || !resultType.Parse(property.Value, out defaultValue) || defaultValue is null)
                {
                    throw context.Logger.Fail($"The map default '{property.Value}' is not a valid {resultType} value.");
                }
                continue;
            }

            if (!sourceType.Parse(property.Key, out var source) || source is null)
            {
                throw context.Logger.Fail($"The map key '{property.Key}' is not a valid {sourceType} value.");
            }
            if (property.Value is null || !resultType.Parse(property.Value, out var result) || result is null)
            {
                throw context.Logger.Fail($"The map value '{property.Value}' is not a valid {resultType} value.");
            }

            entries.Add((source, result));
        }

        if (entries.Count == 0)
        {
            throw context.Logger.Fail("The map processor requires at least one mapping.");
        }

        map.SourceType = sourceType;
        map.ResultType = resultType;
        map.Default = defaultValue;
        map.Entries.AddRange(entries);
    }

    private static object Normalize(object value, DataTypeDescriptor type, ValueProcessorContext context)
    {
        var clrType = type.ClrType;
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
    public IReadOnlyList<PropertyDescriptor> Properties => [ToProperty, DefaultProperty];
}
