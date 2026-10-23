using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class ScaleProcessor : ProcessorBase, IValueProcessor
{
    private static readonly PropertyDescriptor ScaleProperty = new("scale", "sys.decimal", false);
    private static readonly PropertyDescriptor ShiftProperty = new("shift", "sys.decimal", false);

    // logical value = raw value / scale + shift
    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (scale, shift) = GetScaleAndShift(context);
        var logical = Convert.ToDecimal(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);
        var raw = Math.Round((logical - shift) * scale, MidpointRounding.AwayFromZero);
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, logical, raw);

        return new(logicalValue.Name, typeof(long), Convert.ToInt64(raw));
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (scale, shift) = GetScaleAndShift(context);
        var raw = Convert.ToDecimal(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);

        var newValue = (raw/scale) + shift;
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, shift, raw, newValue);

        return new(logicalValue.Name, typeof(decimal), newValue);
    }

    private static (decimal scale, decimal shift) GetScaleAndShift(ValueProcessorContext context)
    {
        decimal? scale = null;
        decimal? shift = null;

        // either all from processor properties
        if (context.Properties.TryGet<decimal>(ScaleProperty, out var scaleProp))
        {
            scale = scaleProp;
        }
        if (context.Properties.TryGet<decimal>(ShiftProperty, out var shiftProp))
        {
            shift = shiftProp;
        }

        // or all from datatype properties
        if (scale is null && shift is null)
        {
            scale = context.DataType?.Scale;
            shift = context.DataType?.Shift;
        }
        
        if (scale is null && shift is null)
        {
            throw context.Logger.Fail("Either the 'scale' or the 'shift' property is required (or the data type must define a scale and/or shift).");
        }

        scale ??= 1;
        shift ??= 0;

        if (scale == 0)
        {
            throw context.Logger.Fail($"Invalid scale value '{scale}'.");
        }

        return (scale.Value, shift.Value);
    }

    public ProcessorKey Key => new("sys.scale");
    public string Name => "Scale & Shift Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [ScaleProperty, ShiftProperty];
}