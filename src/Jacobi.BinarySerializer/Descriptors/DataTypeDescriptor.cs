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

    /// <summary>The field may specify a length.</summary>
    public bool SupportsLength { get; init; }

    /// <summary>Without a length the field takes the rest of the enclosing size window.</summary>
    public bool TakesRestOfWindow { get; init; }

    /// <summary>The type has a default representation (<see cref="Encode"/> and <see cref="Decode"/> are set).</summary>
    public bool HasDefaultRepresentation => Encode is not null && Decode is not null;

    /// <summary>
    /// Creates a descriptor for an enum: the schema literal is the (case-insensitive) member name.
    /// </summary>
    public static DataTypeDescriptor ForEnum<T>(SchemaName name) where T : struct, Enum
        => new(name, typeof(T), (string? text, out object? value) =>
        {
            value = null;
            if (text is not null && Enum.TryParse<T>(text, true, out var parsed) && Enum.IsDefined(parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        });

    public override string ToString() => Name.FullName;
}
