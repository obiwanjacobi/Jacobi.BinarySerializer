using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class ScaleProcessor : IValueProcessor
{
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

        return new(logicalValue.Name, typeof(decimal), raw / scale);
    }

    private static decimal GetScale(ValueProcessorContext context)
    {
        var property = context.Properties.Find("scale")
            ?? throw new InvalidOperationException("The 'scale' property is required by the scale processor.");

        if (!Decimal.TryParse(property.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var scale)
            || scale == 0)
        {
            throw new InvalidOperationException($"Invalid scale value '{property.Value}'.");
        }

        return scale;
    }

    public ProcessorKey Key => new("sys.scale");
    public string Name => "Scale Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [new("scale", typeof(decimal), true)];
}