using System.Collections;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Property lookup scoped to one processor. Full property names are '{namespace}.{id}.{name}'.
/// Lookup by short name is accepted as a fallback (processor-defs may omit the prefix).
/// </summary>
public sealed class ProcessorProperties : IReadOnlyList<SchemaProperty>
{
    private readonly IReadOnlyList<SchemaProperty> _properties;
    private readonly ProcessorKey? _owner;
    private readonly IDataTypeRegistry? _dataTypes;

    public ProcessorProperties(IReadOnlyList<SchemaProperty> properties, ProcessorKey? owner, IDataTypeRegistry? dataTypes = null)
    {
        _properties = properties;
        _owner = owner;
        _dataTypes = dataTypes;
    }

    public int Count => _properties.Count;
    public SchemaProperty this[int index] => _properties[index];

    public IEnumerator<SchemaProperty> GetEnumerator() => _properties.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The full (prefixed) name for a short property name.</summary>
    public string FullName(string propertyName)
        => _owner is { } key ? key.PropertyName(propertyName) : propertyName;

    /// <summary>
    /// Enumerates (short name, value) pairs: this processor's prefix is stripped, unprefixed names pass through,
    /// and properties prefixed for another processor are skipped.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string?>> ShortNames()
    {
        var prefix = _owner is { } key ? key.PropertyName(String.Empty) : null;
        foreach (var property in _properties)
        {
            if (prefix is not null && property.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return new(property.Name[prefix.Length..], property.Value);
            }
            else if (!property.Name.Contains('.'))
            {
                yield return new(property.Name, property.Value);
            }
        }
    }

    public SchemaProperty? Find(string name)
    {
        var full = FullName(name);
        return _properties.FirstOrDefault(p => p.Name.Equals(full, StringComparison.OrdinalIgnoreCase))
            ?? _properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the value of the property described by <paramref name="descriptor"/>, parsed by its data type.
    /// Throws when the property is absent (a required property gets a 'required' message), its value is invalid or <typeparamref name="T"/> does not match the data type.
    /// </summary>
    public T Get<T>(PropertyDescriptor descriptor)
        => Find(descriptor.Name)?.Value is { } text
            ? Parse<T>(descriptor, text)
            : throw (descriptor.IsRequired ? Required(descriptor) : NotFound(descriptor));

    private InvalidOperationException Required(PropertyDescriptor descriptor)
        => new($"The '{FullName(descriptor.Name)}' property is required" +
            (_owner is { } key ? $" by the '{key}' processor." : "."));

    private InvalidOperationException NotFound(PropertyDescriptor descriptor)
        => new($"The '{FullName(descriptor.Name)}' property was not found.");

    /// <summary>
    /// Gets the parsed value of the property, or <paramref name="defaultValue"/> when the property is absent.
    /// Throws when the value is present but invalid, or when the property is absent and required.
    /// </summary>
    public T? GetOrDefault<T>(PropertyDescriptor descriptor, T? defaultValue = default)
        => Find(descriptor.Name)?.Value is { } text
            ? Parse<T>(descriptor, text)
            : descriptor.IsRequired ? throw Required(descriptor) : defaultValue;

    /// <summary>
    /// Returns false when the property is absent, its value is invalid or <typeparamref name="T"/> does not match the data type. Never throws.
    /// </summary>
    public bool TryGet<T>(PropertyDescriptor descriptor, out T value)
    {
        value = default!;
        if (Find(descriptor.Name)?.Value is not { } text)
        {
            return false;
        }

        try
        {
            value = Parse<T>(descriptor, text);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private T Parse<T>(PropertyDescriptor descriptor, string text)
    {
        var dataType = _dataTypes?.TryGet(new SchemaName(descriptor.DataType.FullName), out var found) == true ? found
            : throw new InvalidOperationException(
                $"The data type '{descriptor.DataType}' of the '{FullName(descriptor.Name)}' property is not registered.");

        if (!dataType.Parse(text, out var parsed))
        {
            throw new InvalidOperationException($"Invalid '{FullName(descriptor.Name)}' value '{text}' for data type '{descriptor.DataType}'.");
        }

        return parsed is T typed
            ? typed
            : throw new InvalidOperationException(
                $"The '{FullName(descriptor.Name)}' property is of type '{dataType.ClrType}', not '{typeof(T)}'.");
    }
}
