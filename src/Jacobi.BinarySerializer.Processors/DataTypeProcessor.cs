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
    // - options: the physical value must be one of the options and reads as its logical value (of the value type);
    //   a physical value that is not one of the options reads as the options default (an error without one).
    // - scale/shift: logical value = physical value * scale + shift (a decimal).
    // - min/max: the logical value (converted to the value type) must be in range, also an option value or the options default;
    //   strings compare ordinal, other types with IComparable.
    // options and scale/shift cannot be combined and the scale is not 0 (checked when the data type is registered).
    // write direction: the result is a value of the physical (CLR) type of the data type.
    // the engine converts the logical value to and from the value type of the data type at the boundary with the value model.

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var dataType = GetDataType(context);
        if (dataType.Options is { } options)
        {
            CheckRange(logicalValue.Value, dataType, context);
            var physical = OptionToPhysical(logicalValue.Value, options, dataType, context);
            return new(logicalValue.Name, dataType.ClrType, physical);
        }

        CheckRange(logicalValue.Value, dataType, context);
        if (dataType.Scale is null && dataType.Shift is null)
        {
            return logicalValue;
        }

        var logical = ToDecimal(logicalValue.Value, dataType, context);
        var (scale, shift) = GetScaleAndShift(dataType);
        var raw = Math.Round((logical - shift) / scale, MidpointRounding.AwayFromZero);
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, logical, raw);

        if (!dataType.TryConvert(raw, out var rawValue))
        {
            throw context.Logger.Fail($"The scaled value '{raw}' does not fit {dataType}.");
        }
        return new(logicalValue.Name, dataType.ClrType, rawValue);
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var dataType = GetDataType(context);
        if (dataType.Options is { } options)
        {
            var valueType = dataType.ValueType;
            if (dataType.TryConvert(logicalValue.Value, out var key) && key is not null && options.TryGetValue(key, out var option))
            {
                CheckRange(option, dataType, context);
                return new(logicalValue.Name, valueType.ClrType, option);
            }
            if (dataType.OptionsDefault is { } defaultValue)
            {
                CheckRange(defaultValue, dataType, context);
                return new(logicalValue.Name, valueType.ClrType, defaultValue);
            }
            throw context.Logger.Fail($"Value '{logicalValue.Value}' is not one of the options of {dataType} and it has no options default.");
        }

        if (dataType.Scale is null && dataType.Shift is null)
        {
            CheckRange(logicalValue.Value, dataType, context);
            return logicalValue;
        }

        var physical = ToDecimal(logicalValue.Value, dataType, context);
        var (scale, shift) = GetScaleAndShift(dataType);
        var logical = (physical * scale) + shift;
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, physical, logical);
        CheckRange(logical, dataType, context);

        return new(logicalValue.Name, typeof(decimal), logical);
    }

    private static DataTypeDescriptor GetDataType(ValueProcessorContext context)
        => context.DataType
            ?? throw context.Logger.Fail("The data type processor requires a field (or a repeat/choice value type).");

    private static (decimal scale, decimal shift) GetScaleAndShift(DataTypeDescriptor dataType)
        => (dataType.Scale ?? 1, dataType.Shift ?? 0);

    // a logical value selects its option.
    // the options default has no physical value, so it cannot be written (unless it is also an option value).
    private static object OptionToPhysical(object value, IReadOnlyDictionary<object, object> options, DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        if (dataType.ValueType.TryConvert(value, out var logical))
        {
            foreach (var option in options)
            {
                if (Equals(option.Value, logical))
                {
                    return option.Key;
                }
            }
        }
        throw context.Logger.Fail($"Value '{value}' is not one of the options of {dataType}.");
    }

    private static void CheckRange(object value, DataTypeDescriptor dataType, ValueProcessorContext context)
    {
        if (!dataType.IsInRange(value))
        {
            throw context.Logger.Fail($"Value '{Format(value)}' is out of the range [{Format(dataType.Min)}, {Format(dataType.Max)}] of {dataType}.");
        }
    }

    private static string Format(object? value)
        => value is null ? "..." : Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;

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
