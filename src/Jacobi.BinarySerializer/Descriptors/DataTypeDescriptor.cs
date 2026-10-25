using System.Globalization;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Descriptors;

/// <summary>Parses a schema literal into a value of the descriptor's <see cref="DataTypeDescriptor.ClrType"/>.</summary>
public delegate bool DataTypeParser(string? text, out object? value);

/// <summary>Encodes a value to the default (canonical) binary form of the data type.</summary>
public delegate bool DataTypeEncoder(object? value, out byte[] bytes);

/// <summary>Decodes the default (canonical) binary form of the data type into a value.</summary>
public delegate bool DataTypeDecoder(byte[] bytes, out object? value);

/// <summary>
/// Describes one logical data type that can be used for a schema field.
/// Immutable and stateless: register one instance per type in a <see cref="DataTypeRegistry"/>.
/// </summary>
public sealed class DataTypeDescriptor
{
    public DataTypeDescriptor(SchemaName name, Type clrType, DataTypeParser parse)
    {
        Name = name;
        ClrType = clrType;
        Parse = parse;
    }

    /// <summary>The unique name of the data type (namespaced, for example 'sys.uint16').</summary>
    public SchemaName Name { get; }

    /// <summary>The CLR type of the logical value.</summary>
    public Type ClrType { get; }

    /// <summary>Parses a schema literal (constant) into a value.</summary>
    public DataTypeParser Parse { get; }

    /// <summary>
    /// Encodes a value to the default representation.
    /// Null when the type has no default representation: a representation processor is required.
    /// </summary>
    public DataTypeEncoder? Encode { get; init; }

    /// <summary>Decodes the default representation. Null when <see cref="Encode"/> is null.</summary>
    public DataTypeDecoder? Decode { get; init; }

    /// <summary>The size in bytes of the default representation, or null when it is variable.</summary>
    public int? FixedSize { get; init; }

    /// <summary>The field may specify a physical length in bytes.</summary>
    public bool SupportsLength { get; init; }

    /// <summary>Without a length the field takes the rest of the enclosing size window.</summary>
    public bool TakesRestOfWindow { get; init; }

    /// <summary>The type has a default representation (<see cref="Encode"/> and <see cref="Decode"/> are set).</summary>
    public bool HasDefaultRepresentation => Encode is not null && Decode is not null;

    /// <summary>
    /// Facet: the smallest allowed logical value (inclusive; a value of <see cref="ValueType"/>), or null when unrestricted.
    /// Values are compared with <see cref="Compare"/>.
    /// </summary>
    public object? Min { get; init; }

    /// <summary>Facet: the largest allowed logical value (inclusive; a value of <see cref="ValueType"/>), or null when unrestricted.</summary>
    public object? Max { get; init; }

    /// <summary>Facet: the physical value is divided by this to get the logical value, or null when not scaled.</summary>
    public decimal? Scale { get; init; }

    /// <summary>Facet: offset added to the (scaled) physical value to get the logical value, or null when not shifted.</summary>
    public decimal? Shift { get; init; }

    /// <summary>
    /// Facet: the allowed physical values (of <see cref="ClrType"/>) mapped to their logical values (of <see cref="ValueType"/>),
    /// or null when unrestricted.
    /// </summary>
    public IReadOnlyDictionary<object, object>? Options { get; init; }

    /// <summary>
    /// The data type of the value the value model sees (the logical value); this type itself when the value is not transformed.
    /// The physical part of this descriptor (<see cref="ClrType"/>, <see cref="Encode"/>, <see cref="FixedSize"/>, ...) describes the value that is encoded.
    /// </summary>
    public DataTypeDescriptor ValueType
    {
        get => _valueType ?? this;
        init => _valueType = value;
    }
    private readonly DataTypeDescriptor? _valueType;

    /// <summary>The logical value read for a physical value that is not one of the <see cref="Options"/>, or null when that is an error.</summary>
    public object? OptionsDefault { get; init; }

    /// <summary>The type has at least one facet (<see cref="Min"/>, <see cref="Max"/>, <see cref="Scale"/>, <see cref="Shift"/> or <see cref="Options"/>).</summary>
    public bool HasFacets => Min is not null || Max is not null || Scale is not null || Shift is not null || Options is not null;

    /// <summary>
    /// Creates a descriptor for an enum: the schema literal is the member name, case-insensitive and ignoring '-' and '_'
    /// (so 'crc32-bzip2' matches the member Crc32Bzip2).
    /// </summary>
    public static DataTypeDescriptor ForEnum<T>(SchemaName name) where T : struct, Enum
        => new(name, typeof(T), (string? text, out object? value) =>
        {
            value = null;
            var normalized = text?.Replace("-", String.Empty).Replace("_", String.Empty);
            if (normalized is not null && Enum.TryParse<T>(normalized, true, out var parsed) && Enum.IsDefined(parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        });

    /// <summary>
    /// Creates a descriptor based on this one: it inherits the CLR type, parse, encode/decode and size.
    /// Facets of the definition override the inherited ones; options of the definition replace the inherited options (and their default).
    /// </summary>
    /// <param name="valueType">The resolved value type of the new descriptor, or null to inherit the value type of this one.</param>
    public DataTypeDescriptor Derive(SchemaName name, SchemaDataTypeDef definition, DataTypeDescriptor? valueType = null)
    {
        valueType ??= ValueType;
        var options = Options;
        var defaultValue = OptionsDefault;
        if (definition.Options is not null)
        {
            options = ParseOptions(name, definition.Options, valueType);
            defaultValue = null;
        }
        else if (options is not null && valueType.ClrType != ValueType.ClrType)
        {
            options = ConvertOptions(name, options, valueType);
            defaultValue = defaultValue is null ? null : ConvertOption(name, defaultValue, valueType);
        }

        if (definition.OptionsDefault is { } defaultText)
        {
            if (options is null)
            {
                throw new InvalidOperationException($"The data type '{name}' has an options default but no options.");
            }
            if (!valueType.Parse(defaultText, out defaultValue) || defaultValue is null)
            {
                throw new InvalidOperationException($"The options default '{defaultText}' of data type '{name}' is not a valid {valueType} value.");
            }
        }

        var min = ParseBound(name, "min", definition.Min, valueType) ?? InheritBound(name, Min, valueType);
        var max = ParseBound(name, "max", definition.Max, valueType) ?? InheritBound(name, Max, valueType);
        if ((min is not null || max is not null) && !typeof(IComparable).IsAssignableFrom(valueType.ClrType))
        {
            throw new InvalidOperationException($"The data type '{name}' has a min/max, but the values of its value type {valueType} cannot be compared.");
        }
        if (min is not null && max is not null && Compare(min, max) > 0)
        {
            throw new InvalidOperationException($"The min '{min}' of data type '{name}' is larger than its max '{max}'.");
        }

        return new(name, ClrType, Parse)
        {
            Encode = Encode,
            Decode = Decode,
            FixedSize = FixedSize,
            SupportsLength = SupportsLength,
            TakesRestOfWindow = TakesRestOfWindow,
            Min = min,
            Max = max,
            Scale = definition.Scale ?? Scale,
            Shift = definition.Shift ?? Shift,
            Options = options,
            ValueType = valueType,
            OptionsDefault = defaultValue,
        };
    }

    // physical values are parsed with this (base) type, logical values with the options type.
    private Dictionary<object, object> ParseOptions(SchemaName name, IReadOnlyDictionary<string, string> definitions, DataTypeDescriptor valueType)
    {
        var options = new Dictionary<object, object>();
        foreach (var (physicalText, logicalText) in definitions)
        {
            if (!Parse(physicalText, out var physical) || physical is null)
            {
                throw new InvalidOperationException($"The option '{physicalText}' of data type '{name}' is not a valid {this} value.");
            }
            if (!valueType.Parse(logicalText, out var logical) || logical is null)
            {
                throw new InvalidOperationException($"The option value '{logicalText}' of data type '{name}' is not a valid {valueType} value.");
            }
            if (!options.TryAdd(physical, logical))
            {
                throw new InvalidOperationException($"The option '{physicalText}' of data type '{name}' is defined more than once.");
            }
        }
        return options;
    }

    private static object? ParseBound(SchemaName name, string facet, string? text, DataTypeDescriptor valueType)
    {
        if (text is null)
        {
            return null;
        }
        return valueType.Parse(text, out var bound) && bound is not null
            ? bound
            : throw new InvalidOperationException($"The {facet} '{text}' of data type '{name}' is not a valid {valueType} value.");
    }

    // an inherited bound with a different value type.
    private static object? InheritBound(SchemaName name, object? bound, DataTypeDescriptor valueType)
        => bound is null ? null
            : valueType.TryConvert(bound, out var converted) && converted is not null ? converted
            : throw new InvalidOperationException($"The inherited min/max '{bound}' of data type '{name}' cannot be converted to {valueType}.");

    /// <summary>
    /// Checks a logical value against <see cref="Min"/> and <see cref="Max"/>; the value is converted to the <see cref="ValueType"/> first.
    /// False when the value is out of range or cannot be converted.
    /// </summary>
    public bool IsInRange(object value)
    {
        if (Min is null && Max is null)
        {
            return true;
        }
        if (!ValueType.TryConvert(value, out var logical) || logical is null)
        {
            return false;
        }
        return (Min is null || Compare(logical, Min) >= 0) && (Max is null || Compare(logical, Max) <= 0);
    }

    /// <summary>Compares two values of the same type: strings ordinal, other types with <see cref="IComparable"/>.</summary>
    public static int Compare(object x, object y)
        => x is string left && y is string right
            ? String.CompareOrdinal(left, right)
            : ((IComparable)x).CompareTo(y);

    // inherited options with a different value type.
    private static Dictionary<object, object> ConvertOptions(SchemaName name, IReadOnlyDictionary<object, object> inherited, DataTypeDescriptor valueType)
        => inherited.ToDictionary(option => option.Key, option => ConvertOption(name, option.Value, valueType));

    private static object ConvertOption(SchemaName name, object value, DataTypeDescriptor valueType)
        => valueType.TryConvert(value, out var converted) && converted is not null
            ? converted
            : throw new InvalidOperationException($"The inherited option value '{value}' of data type '{name}' cannot be converted to {valueType}.");

    /// <summary>
    /// Converts a value to the <see cref="ClrType"/> of this type: a value of that type as is, an enum from its name or number, otherwise <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>.
    /// Null converts to null.
    /// </summary>
    public bool TryConvert(object? value, out object? converted)
    {
        converted = value;
        if (value is null || ClrType.IsInstanceOfType(value))
        {
            return true;
        }

        try
        {
            if (ClrType.IsEnum)
            {
                if (value is string text)
                {
                    return Parse(text, out converted);
                }
                converted = Enum.ToObject(ClrType, value);
                return true;
            }
            converted = Convert.ChangeType(value, ClrType, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            converted = null;
            return false;
        }
    }

    public override string ToString() => Name.FullName;
}
