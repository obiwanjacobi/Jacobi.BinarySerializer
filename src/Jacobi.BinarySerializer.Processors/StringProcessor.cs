using System.Globalization;
using System.Text;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Strings (sys:string). Exactly one of 'length' or 'terminator' delimits the string.
/// 'length' is a fixed byte count: shorter strings are padded with the 'padding' byte (default 0), which is trimmed again on read.
/// 'terminator' is a single byte value that ends the string and is not part of it.
/// The 'encoding' property names the text encoding (default 'utf-8'). See also <see cref="System.Text.Encoding.WebName"/>.
/// 'validBytes' restricts the bytes of the string (padding included, terminator excluded) to a list of byte ranges.
/// Every other property is a character map entry: the name is a byte value, the value the text element it stands for.
/// The map replaces those bytes of a single-byte encoding (device character sets).
/// </summary>
internal sealed class StringProcessor : ProcessorBase, IFieldProcessor
{
    /// <summary>The name under which the string length (number of chars, int) is published, in the namespace 'sys.string' by default.</summary>
    public const string PublishedLength = "length";

    private static readonly PropertyDescriptor EncodingProperty = new("encoding", "sys.string", false, description: "The text encoding name; 'utf-8' by default.");
    private static readonly PropertyDescriptor ByteLengthProperty = new("byteLength", "sys.int32", false, description: "A fixed length in bytes; shorter strings are padded. Exclusive with 'terminator'.");
    private static readonly PropertyDescriptor TerminatorProperty = new("terminator", "sys.uint8", false, description: "The byte value (0-255) that ends the string. Exclusive with 'byteLength'.");
    private static readonly PropertyDescriptor PaddingProperty = new("padding", "sys.uint8", false, description: "The byte value (0-255) that pads a fixed length; 0 by default. Trailing padding is trimmed on read.");
    private static readonly PropertyDescriptor LengthProperty = new(PublishedLength, "sys.int32", false, isReadOnly: true, isPublished: true, description: "Published: the length of the string in chars.");
    private static readonly PropertyDescriptor ValidBytesProperty = new("validBytes", "sys.string", false, description: "The byte values the string may hold, as a comma separated list of values and ranges (e.g. '0x16-0x7F'); all by default.");

    // the property names that are not character map entries
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        EncodingProperty.Name, ByteLengthProperty.Name, TerminatorProperty.Name, PaddingProperty.Name, LengthProperty.Name, ValidBytesProperty.Name
    };

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
        var options = ReadOptions(context);
        CheckType(context, path);

        if (field.Value is not string text)
        {
            throw new InvalidOperationException($"'{path}': the string processor expects a string, not '{field.Value ?? "null"}'.");
        }

        context.Publish(PublishedLength, text.Length);
        var bytes = Encode(text, options, path);
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

            options.CharacterSet.CheckBytes(bytes, bytes.Length, path);
            Array.Resize(ref bytes, bytes.Length + 1);
            bytes[^1] = terminator;
        }

        if (options.Length is not null)
        {
            options.CharacterSet.CheckBytes(bytes, bytes.Length, path);
        }

        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    private static FieldReadResult<LogicalField> ReadCore(EncodedField field, FieldProcessorContext context)
    {
        var path = context.Field.Path;
        var options = ReadOptions(context);
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

        // the bytes of the field: padding included, terminator excluded
        options.CharacterSet.CheckBytes(bytes, options.Length ?? textLength, path);
        var text = Decode(bytes, textLength, options, path);

        context.Publish(PublishedLength, text.Length);
        return FieldReadResult<LogicalField>.Consumed(new(field.Name, typeof(string), text), consumed * 8);
    }

    private static byte[] Encode(string text, Options options, string path)
    {
        var characterSet = options.CharacterSet;
        try
        {
            if (characterSet.ByByte.Count == 0)
            {
                return options.Encoding.GetBytes(text);
            }

            var bytes = new List<byte>(text.Length);
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (characterSet.ByText.TryGetValue(element, out var mapped))
                {
                    bytes.Add(mapped);
                    continue;
                }

                foreach (var b in options.Encoding.GetBytes(element))
                {
                    // the byte stands for another character: this one cannot be written
                    if (characterSet.ByByte.TryGetValue(b, out var other))
                    {
                        throw new InvalidOperationException(
                            $"'{path}': '{element}' is not in the character set; its byte 0x{b:X2} stands for '{other}'.");
                    }
                    bytes.Add(b);
                }
            }
            return [.. bytes];
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException($"'{path}': the text '{text}' cannot be encoded as {options.Encoding.WebName}.", ex);
        }
    }

    private static string Decode(byte[] bytes, int length, Options options, string path)
    {
        var characterSet = options.CharacterSet;
        try
        {
            if (characterSet.ByByte.Count == 0)
            {
                return options.Encoding.GetString(bytes, 0, length);
            }

            // decode the runs of unmapped bytes with the (single-byte) encoding
            var text = new StringBuilder(length);
            var start = 0;
            for (var i = 0; i < length; i++)
            {
                if (characterSet.ByByte.TryGetValue(bytes[i], out var mapped))
                {
                    text.Append(options.Encoding.GetString(bytes, start, i - start)).Append(mapped);
                    start = i + 1;
                }
            }
            return text.Append(options.Encoding.GetString(bytes, start, length - start)).ToString();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException($"'{path}': the bytes are not valid {options.Encoding.WebName} text.", ex);
        }
    }

    private static void CheckType(FieldProcessorContext context, string path)
    {
        if (context.Field.DataType.ClrType != typeof(string))
        {
            throw new InvalidOperationException($"'{path}': the string processor requires a String field, not {context.Field.DataType.Name}.");
        }
    }

    private static Options ReadOptions(FieldProcessorContext context)
    {
        var properties = context.Properties;
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

        int? length = properties.TryGet<int>(ByteLengthProperty, out var lengthValue) ? lengthValue : null;
        if (length < 1)
        {
            throw new InvalidOperationException($"Invalid '{properties.FullName(ByteLengthProperty.Name)}' value '{length}'. Expected a number from 1 to {Int32.MaxValue}.");
        }
        if (context.FieldData.ByteLength is { } fieldLength)
        {
            if (length is not null)
            {
                throw new InvalidOperationException(
                    $"The field has a byte length; the '{properties.FullName(ByteLengthProperty.Name)}' property cannot be used as well.");
            }
            if (fieldLength < 1)
            {
                throw new InvalidOperationException($"Invalid field length '{fieldLength}'. Expected a number from 1 to {Int32.MaxValue}.");
            }
            length = fieldLength;
        }
        byte? terminator = properties.TryGet<byte>(TerminatorProperty, out var terminatorValue) ? terminatorValue : null;
        var padding = properties.GetOrDefault<byte>(PaddingProperty);

        if (length is null == terminator is null)
        {
            throw new InvalidOperationException(
                $"The string processor requires exactly one of the field byte length, '{properties.FullName(ByteLengthProperty.Name)}' or '{properties.FullName(TerminatorProperty.Name)}'.");
        }

        var characterSet = context.GetOrCreateState<CharacterSet>();
        if (!characterSet.IsParsed)
        {
            characterSet.Parse(properties, encoding);
        }

        return new Options(encoding, length, terminator, padding, characterSet);
    }

    private sealed record Options(Encoding Encoding, int? Length, byte? Terminator, byte Padding, CharacterSet CharacterSet);

    /// <summary>
    /// The character map ('byte' = 'text element' properties) and the valid bytes ('validBytes') of one processor binding,
    /// parsed once from the properties.
    /// </summary>
    private sealed class CharacterSet
    {
        private readonly List<(byte From, byte To)> _validBytes = [];

        public bool IsParsed { get; private set; }
        public Dictionary<byte, string> ByByte { get; } = [];
        public Dictionary<string, byte> ByText { get; } = new(StringComparer.Ordinal);

        public void Parse(ProcessorProperties properties, Encoding encoding)
        {
            if (properties.GetOrDefault<string>(ValidBytesProperty) is { } validBytes)
            {
                ParseValidBytes(validBytes, properties.FullName(ValidBytesProperty.Name));
            }

            foreach (var (name, value) in properties.ShortNames())
            {
                if (KnownNames.Contains(name))
                {
                    continue;
                }

                var fullName = properties.FullName(name);
                if (!TryParseByte(name, out var b))
                {
                    throw new InvalidOperationException(
                        $"Invalid property '{fullName}': not a property of the string processor, nor a byte value (0-255) of a character map entry.");
                }
                if (value is null || new StringInfo(value).LengthInTextElements != 1)
                {
                    throw new InvalidOperationException($"Invalid '{fullName}' value '{value}': a character map entry is one text element.");
                }
                if (!IsValid(b))
                {
                    throw new InvalidOperationException($"Invalid property '{fullName}': the byte is not in '{properties.FullName(ValidBytesProperty.Name)}'.");
                }
                if (!ByByte.TryAdd(b, value))
                {
                    throw new InvalidOperationException($"Invalid property '{fullName}': the byte 0x{b:X2} is mapped more than once.");
                }
                if (!ByText.TryAdd(value, b))
                {
                    throw new InvalidOperationException($"Invalid property '{fullName}': '{value}' is mapped more than once.");
                }
            }

            if (ByByte.Count > 0 && !encoding.IsSingleByte)
            {
                throw new InvalidOperationException(
                    $"A character map requires a single-byte encoding, not {encoding.WebName}; set '{properties.FullName(EncodingProperty.Name)}'.");
            }

            IsParsed = true;
        }

        public void CheckBytes(byte[] bytes, int length, string path)
        {
            for (var i = 0; i < length; i++)
            {
                if (!IsValid(bytes[i]))
                {
                    throw new InvalidOperationException($"'{path}': the byte 0x{bytes[i]:X2} is not a valid byte of the string.");
                }
            }
        }

        private bool IsValid(byte b)
            => _validBytes.Count == 0 || _validBytes.Exists(range => b >= range.From && b <= range.To);

        private void ParseValidBytes(string text, string fullName)
        {
            foreach (var item in text.Split(',', StringSplitOptions.TrimEntries))
            {
                var bounds = item.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length is < 1 or > 2
                    || !TryParseByte(bounds[0], out var from)
                    || !TryParseByte(bounds[^1], out var to)
                    || from > to)
                {
                    throw new InvalidOperationException(
                        $"Invalid '{fullName}' value '{text}'. Expected a comma separated list of byte values (0-255) and ranges (e.g. '0x16-0x7F').");
                }
                _validBytes.Add((from, to));
            }
        }

        private static bool TryParseByte(string text, out byte value)
            => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Byte.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                : Byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public ProcessorKey Key => new("sys.string");
    public string Name => "String Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties => [EncodingProperty, ByteLengthProperty, TerminatorProperty, PaddingProperty, ValidBytesProperty, LengthProperty];
}
