using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class ScaleProcessor : IValueProcessor
{
    private static readonly PropertyDescriptor ScaleProperty = new("scale", "sys.decimal", true);

    // logical value = raw value / scale
    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var scale = GetScale(context);
        var logical = Convert.ToDecimal(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);
        var raw = Math.Round(logical * scale, MidpointRounding.AwayFromZero);
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, logical, raw);

        return new(logicalValue.Name, typeof(long), Convert.ToInt64(raw));
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var scale = GetScale(context);
        var raw = Convert.ToDecimal(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);
        context.Logger.Scaled(context.Field?.Path ?? String.Empty, scale, raw, raw / scale);

        return new(logicalValue.Name, typeof(decimal), raw / scale);
    }

    private static decimal GetScale(ValueProcessorContext context)
    {
        var scale = context.Properties.Get<decimal>(ScaleProperty);
        if (scale == 0)
        {
            throw context.Logger.Fail($"Invalid scale value '{scale}'.");
        }

        return scale;
    }

    public ProcessorKey Key => new("sys.scale");
    public string Name => "Scale Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [ScaleProperty];
}