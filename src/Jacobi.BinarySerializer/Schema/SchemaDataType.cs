using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// The name of the (logical) data type of a field or nodeDef.
/// It is resolved to a <see cref="Descriptors.DataTypeDescriptor"/> in the <see cref="Descriptors.DataTypeRegistry"/>.
/// </summary>
/// <remarks>
/// A name without a namespace is a built-in type (case-insensitive): 'Int32' is 'sys.int32'.
/// Use a nullable <c>SchemaDataType?</c> where a data type is optional (for group nodeDefs).
/// </remarks>
[JsonConverter(typeof(SchemaDataTypeJsonConverter))]
public readonly struct SchemaDataType : IEquatable<SchemaDataType>
{
    private const string SystemPrefix = "sys.";

    private readonly string? _name;

    public SchemaDataType(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = Normalize(name);
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (!name.Contains(SchemaName.Separator))
        {
            return SystemPrefix + name.ToLowerInvariant();
        }
        return name.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase)
            ? SystemPrefix + name[SystemPrefix.Length..].ToLowerInvariant()
            : name;
    }

    /// <summary>The full (namespaced) name.</summary>
    public string FullName => _name ?? System.String.Empty;

    /// <summary>The name as a <see cref="SchemaName"/>.</summary>
    public SchemaName Name => new(FullName);

    public static implicit operator SchemaDataType(string name) => new(name);
    public override string ToString() => FullName;

    public bool Equals(SchemaDataType other) => System.String.Equals(_name, other._name, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is SchemaDataType other && Equals(other);
    public override int GetHashCode() => _name is null ? 0 : _name.GetHashCode(StringComparison.OrdinalIgnoreCase);

    public static bool operator ==(SchemaDataType left, SchemaDataType right) => left.Equals(right);
    public static bool operator !=(SchemaDataType left, SchemaDataType right) => !left.Equals(right);
}

internal sealed class SchemaDataTypeJsonConverter : JsonConverter<SchemaDataType>
{
    public override SchemaDataType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? throw new JsonException("A data type name is expected."));

    public override void Write(Utf8JsonWriter writer, SchemaDataType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.FullName);
}
