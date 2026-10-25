using System.Globalization;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Applies the facets of the field's data type: options, scale/shift and min/max.
/// The plan builder adds it to a field whose data type has facets and that has no semantic processor.
/// </summary>
internal sealed class DataTypeProcessor : ProcessorBase, IValueProcessor
{
    // read direction:
    // - options: the physical value must be one of the options and reads as its name.
    // - scale/shift: logical value = physical value * scale + shift (a decimal).
    // - min/max: the logical numeric value (the physical value with options) must be in range.
    // options and scale/shift cannot be combined.

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var dataType = GetDataType(context);
        if (dataType.Options is { } options)
        {
            var physical = OptionToPhysical(logicalValue.Value, options, dataType, context);
            CheckRange(physical, dataType, context);
            return new(logicalValue.Name, typeof(long), physical);
        }

        var logical = ToDecimal(logicalValue.Value, dataType, context);
        CheckRange(logical, dataType, context);

        if (dataType.Scale is null && dataType.Shift is null)
        {
            return logicalValue;
        }

        var (scale, shift) = GetScaleAndShift(dataType, context);
        var raw = Math.Round((logical - shift) / scale, MidpointRounding.AwayFromZero);
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, logical, raw);

        return new(logicalValue.Name, typeof(long), Convert.ToInt64(raw));
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var dataType = GetDataType(context);
        var physical = ToDecimal(logicalValue.Value, dataType, context);

        if (dataType.Options is { } options)
        {
            CheckRange(physical, dataType, context);
            if (physical != Decimal.Truncate(physical) || !options.TryGetValue((long)physical, out var name))
            {
                throw context.Logger.Fail($"Value '{logicalValue.Value}' is not one of the options of {dataType}.");
            }
            return new(logicalValue.Name, typeof(string), name);
        }

        if (dataType.Scale is null && dataType.Shift is null)
        {
            CheckRange(physical, dataType, context);
            return logicalValue;
        }

        var (scale, shift) = GetScaleAndShift(dataType, context);
        var logical = (physical * scale) + shift;
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, physical, logical);
        CheckRange(logical, dataType, context);

        return new(logicalValue.Name, typeof(decimal), logical);
    }

    private static DataTypeDescriptor GetDataType(ValueProcessorContext context)
    {
        var dataType = context.DataType
            ?? throw context.Logger.Fail("The data type processor requires a field (or a repeat/choice value type).");

        if (dataType.Options is not null && (dataType.Scale is not null || dataType.Shift is not null))
        {
            throw context.Logger.Fail($"The data type {dataType} cannot combine options with a scale or shift.");
        }
        return dataType;
    }

    private static (decimal scale, decimal shift) GetScaleAndShift(DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        var scale = dataType.Scale ?? 1;
        if (scale == 0)
        {
            throw context.Logger.Fail($"Invalid scale value '{scale}' of {dataType}.");
        }
        return (scale, dataType.Shift ?? 0);
    }

    // a name selects its option; a number must be one of the option values.
    private static long OptionToPhysical(object value, IReadOnlyDictionary<long, string> options, DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        if (value is string name)
        {
            foreach (var option in options)
            {
                if (String.Equals(option.Value, name, StringComparison.Ordinal))
                {
                    return option.Key;
                }
            }
            throw context.Logger.Fail($"Value '{name}' is not one of the options of {dataType}.");
        }

        var number = ToDecimal(value, dataType, context);
        if (number != Decimal.Truncate(number) || !options.ContainsKey((long)number))
        {
            throw context.Logger.Fail($"Value '{value}' is not one of the options of {dataType}.");
        }
        return (long)number;
    }

    private static void CheckRange(decimal value, DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        if (value < dataType.Min || value > dataType.Max)
        {
            throw context.Logger.Fail($"Value '{value}' is out of the range [{dataType.Min?.ToString(CultureInfo.InvariantCulture) ?? "..."}, {dataType.Max?.ToString(CultureInfo.InvariantCulture) ?? "..."}] of {dataType}.");
        }
    }

    private static decimal ToDecimal(object value, DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw context.Logger.Fail($"Value '{value}' is not a number; the facets of {dataType} require one.", ex);
        }
    }

    public ProcessorKey Key => new("sys.datatype");
    public string Name => "Data Type Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [];
}
