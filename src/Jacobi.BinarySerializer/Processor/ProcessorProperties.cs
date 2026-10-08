using System.Collections;
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

    public ProcessorProperties(IReadOnlyList<SchemaProperty> properties, ProcessorKey? owner)
    {
        _properties = properties;
        _owner = owner;
    }

    public int Count => _properties.Count;
    public SchemaProperty this[int index] => _properties[index];

    public IEnumerator<SchemaProperty> GetEnumerator() => _properties.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The full (prefixed) name for a short property name.</summary>
    public string FullName(string name)
        => _owner is { } key ? key.PropertyName(name) : name;

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

    public bool TryGet(string name, out string value)
    {
        var property = Find(name);
        value = property?.Value ?? String.Empty;
        return property is not null;
    }

    public string? GetOrDefault(string name) => Find(name)?.Value;

    public string Get(string name)
        => Find(name)?.Value
            ?? throw new InvalidOperationException(
                $"The '{FullName(name)}' property is required" +
                (_owner is { } key ? $" by the '{key}' processor." : "."));

    public bool TryGet<T>(string name, out T value) where T : IParsable<T>
    {
        value = default!;
        return TryGet(name, out var text)
            && T.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value!);
    }

    public T Get<T>(string name) where T : IParsable<T>
    {
        var text = Get(name);
        return T.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"Invalid '{FullName(name)}' value '{text}'.");
    }

    /// <summary>
    /// Gets the value of the property described by <paramref name="descriptor"/>.
    /// Returns the default when the property is absent and not required; throws when it is required.
    /// </summary>
    public T? Get<T>(PropertyDescriptor descriptor) where T : IParsable<T>
    {
        if (!descriptor.ClrType.IsAssignableTo(typeof(T)))
        {
            throw new InvalidOperationException(
                $"The '{FullName(descriptor.Name)}' property is of type '{descriptor.ClrType}', not '{typeof(T)}'.");
        }

        return descriptor.IsRequired || Find(descriptor.Name) is not null
            ? Get<T>(descriptor.Name)
            : default;
    }

    /// <summary>
    /// Gets the text value of the property described by <paramref name="descriptor"/>.
    /// Returns null when the property is absent and not required; throws when it is required.
    /// </summary>
    public string? Get(PropertyDescriptor descriptor)
        => descriptor.IsRequired ? Get(descriptor.Name) : GetOrDefault(descriptor.Name);

    public T GetEnum<T>(string name, T defaultValue) where T : struct, Enum
    {
        var text = GetOrDefault(name);
        if (text is null)
        {
            return defaultValue;
        }

        return Enum.TryParse<T>(text, ignoreCase: true, out var value)
            ? value
            : throw new InvalidOperationException($"Invalid '{FullName(name)}' value '{text}'.");
    }
}
