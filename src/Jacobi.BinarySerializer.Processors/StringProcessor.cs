using System.Text;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Strings (sys:string). Exactly one of 'length' or 'terminator' delimits the string.
/// 'length' is a fixed byte count: shorter strings are padded with the 'padding' byte (default 0), which is trimmed again on read.
/// 'terminator' is a single byte value that ends the string and is not part of it.
/// The 'encoding' property names the text encoding (default 'utf-8').
/// </summary>
internal sealed class StringProcessor : IFieldProcessor
{
    private static readonly PropertyDescriptor EncodingProperty = new("encoding", "sys.string", false, description: "The text encoding name; 'utf-8' by default.");
    private static readonly PropertyDescriptor LengthProperty = new("length", "sys.int32", false, description: "A fixed length in bytes; shorter strings are padded. Exclusive with 'terminator'.");
    private static readonly PropertyDescriptor TerminatorProperty = new("terminator", "sys.uint8", false, description: "The byte value (0-255) that ends the string. Exclusive with 'length'.");
    private static readonly PropertyDescriptor PaddingProperty = new("padding", "sys.uint8", false, description: "The byte value (0-255) that pads a fixed length; 0 by default. Trailing padding is trimmed on read.");

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        try
        {
            return WriteCore(field, context);
        }
        catch (InvalidOperationException ex)
        {
            throw context.Logger.Fail(ex.Message, ex);
        }
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        try
        {
            return ReadCore(field, context);
        }
        catch (InvalidOperationException ex)
        {
            throw context.Logger.Fail(ex.Message, ex);
        }
    }

    private static FieldWriteResult<EncodedField> WriteCore(LogicalField field, FieldProcessorContext context)
    {
        var path = context.Field.Path;
        var options = ReadOptions(context.Properties);
        CheckType(context, path);

        if (field.Value is not string text)
        {
            throw new InvalidOperationException($"'{path}': the string processor expects a string, not '{field.Value ?? "null"}'.");
        }

        var bytes = options.Encoding.GetBytes(text);
        if (options.Length is { } length)
        {
            if (bytes.Length > length)
            {
                throw new InvalidOperationException($"'{path}': the string takes {bytes.Length} bytes; the length is {length}.");
            }

            var textBytes = bytes.Length;
            Array.Resize(ref bytes, length);
            Array.Fill(bytes, options.Padding, textBytes, length - textBytes);
        }
        else
        {
            var terminator = options.Terminator!.Value;
            if (Array.IndexOf(bytes, terminator) >= 0)
            {
                throw new InvalidOperationException($"'{path}': the string contains the terminator byte {terminator}.");
            }

            Array.Resize(ref bytes, bytes.Length + 1);
            bytes[^1] = terminator;
        }

        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    private static FieldReadResult<LogicalField> ReadCore(EncodedField field, FieldProcessorContext context)
    {
        var path = context.Field.Path;
        var options = ReadOptions(context.Properties);
        CheckType(context, path);

        if (field.Value is not byte[] bytes)
        {
            throw new InvalidOperationException($"'{path}': the string processor expects bytes.");
        }

        int textLength;
        int consumed;
        if (options.Length is { } length)
        {
            if (bytes.Length < length)
            {
                return FieldReadResult<LogicalField>.NeedMoreData();
            }

            consumed = length;
            textLength = length;
            while (textLength > 0 && bytes[textLength - 1] == options.Padding)
            {
                textLength--;
            }
        }
        else
        {
            textLength = Array.IndexOf(bytes, options.Terminator!.Value);
            if (textLength < 0)
            {
                return FieldReadResult<LogicalField>.NeedMoreData();
            }
            consumed = textLength + 1;
        }

        string text;
        try
        {
            text = options.Encoding.GetString(bytes, 0, textLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException($"'{path}': the bytes are not valid {options.Encoding.WebName} text.", ex);
        }

        return FieldReadResult<LogicalField>.Consumed(new(field.Name, typeof(string), text), consumed * 8);
    }

    private static void CheckType(FieldProcessorContext context, string path)
    {
        if (context.Field.DataType.ClrType != typeof(string))
        {
            throw new InvalidOperationException($"'{path}': the string processor requires a String field, not {context.Field.DataType.Name}.");
        }
    }

    private static Options ReadOptions(ProcessorProperties properties)
    {
        var encodingName = properties.GetOrDefault<string>(EncodingProperty);
        Encoding encoding;
        try
        {
            encoding = encodingName is null ? new UTF8Encoding(false, true) : Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid '{properties.FullName(EncodingProperty.Name)}' value '{encodingName}'.", ex);
        }

        int? length = properties.TryGet<int>(LengthProperty, out var lengthValue) ? lengthValue : null;
        if (length < 1)
        {
            throw new InvalidOperationException($"Invalid '{properties.FullName(LengthProperty.Name)}' value '{length}'. Expected a number from 1 to {Int32.MaxValue}.");
        }
        byte? terminator = properties.TryGet<byte>(TerminatorProperty, out var terminatorValue) ? terminatorValue : null;
        var padding = properties.GetOrDefault<byte>(PaddingProperty);

        if (length is null == terminator is null)
        {
            throw new InvalidOperationException(
                $"The string processor requires exactly one of '{properties.FullName(LengthProperty.Name)}' or '{properties.FullName(TerminatorProperty.Name)}'.");
        }

        return new Options(encoding, length, terminator, padding);
    }

    private sealed record Options(Encoding Encoding, int? Length, byte? Terminator, byte Padding);

    public ProcessorKey Key => new("sys.string");
    public string Name => "String Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties => [EncodingProperty, LengthProperty, TerminatorProperty, PaddingProperty];
}
