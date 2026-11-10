using System.Collections;
using System.Globalization;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processor;

/// <summary>
/// Property lookup scoped to one processor. Full property names are '{namespace}.{id}.{name}'.
/// Lookup by short name is accepted as a fallback (processor-defs may omit the prefix).
/// </summary>
/// <remarks>
/// A property whose value is a reference ('ref:' or 'pub:') has the published value it refers to, resolved for the current instance.
/// The typed getters convert that value to the data type of the property; the text accessors (the list, <see cref="Find"/>,
/// <see cref="ShortNames"/>) give its invariant text.
/// </remarks>
public sealed class ProcessorProperties : IReadOnlyList<SchemaProperty>
{
    private readonly IReadOnlyList<SchemaProperty> _properties;
    private readonly ProcessorKey? _owner;
    private readonly IDataTypeRegistry? _dataTypes;
    private readonly IReadOnlyDictionary<string, PublishedValueKey>? _references;
    private readonly Func<PublishedValueKey, string, object?>? _resolve;
    private Dictionary<string, object?>? _resolved;

    public ProcessorProperties(IReadOnlyList<SchemaProperty> properties, ProcessorKey? owner, IDataTypeRegistry? dataTypes = null)
    {
        _properties = properties;
        _owner = owner;
        _dataTypes = dataTypes;
    }

    /// <param name="references">The bound references of the properties (by property name).</param>
    /// <param name="resolve">Gets the published value of a reference (the key and the full property name).</param>
    internal ProcessorProperties(IReadOnlyList<SchemaProperty> properties, ProcessorKey? owner, IDataTypeRegistry? dataTypes,
        IReadOnlyDictionary<string, PublishedValueKey> references, Func<PublishedValueKey, string, object?> resolve)
        : this(properties, owner, dataTypes)
    {
        _references = references.Count == 0 ? null : references;
        _resolve = resolve;
    }

    public int Count => _properties.Count;
    public SchemaProperty this[int index] => Text(_properties[index]);

    public IEnumerator<SchemaProperty> GetEnumerator() => _properties.Select(Text).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>True when any property value is a reference (so a value can differ per instance: do not cache what is parsed from it).</summary>
    public bool HasReferences => _references is not null;

    /// <summary>True when the value of the property is a reference ('ref:' or 'pub:') to a published value.</summary>
    public bool IsReference(string name)
        => FindRaw(name) is { } property && IsReference(property);

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
                yield return new(property.Name[prefix.Length..], Text(property).Value);
            }
            else if (!property.Name.Contains('.'))
            {
                yield return new(property.Name, Text(property).Value);
            }
        }
    }

    public SchemaProperty? Find(string name)
        => FindRaw(name) is { } property ? Text(property) : null;

    private SchemaProperty? FindRaw(string name)
    {
        var full = FullName(name);
        return _properties.FirstOrDefault(p => p.Name.Equals(full, StringComparison.OrdinalIgnoreCase))
            ?? _properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsReference(SchemaProperty property)
        => _references?.ContainsKey(property.Name) == true;

    /// <summary>The published value of a reference property (resolved once per lookup object).</summary>
    private object? Resolve(SchemaProperty property)
    {
        _resolved ??= new(StringComparer.OrdinalIgnoreCase);
        if (!_resolved.TryGetValue(property.Name, out var value))
        {
            var key = _references![property.Name];
            value = _resolve is null
                ? throw new InvalidOperationException($"The '{property.Name}' property refers to '{key}', which cannot be resolved here.")
                : _resolve(key, property.Name);
            _resolved[property.Name] = value;
        }
        return value;
    }

    /// <summary>The property with its value as text: the resolved value of a reference (invariant culture).</summary>
    private SchemaProperty Text(SchemaProperty property)
        => IsReference(property) ? new SchemaProperty { Name = property.Name, Value = FormatValue(Resolve(property)) } : property;

    private static string? FormatValue(object? value)
        => value switch
        {
            null => null,
            string text => text,
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    /// <summary>
    /// Gets the value of the property described by <paramref name="descriptor"/>, parsed by its data type.
    /// Throws when the property is absent (a required property gets a 'required' message), its value is invalid or <typeparamref name="T"/> does not match the data type.
    /// </summary>
    public T Get<T>(PropertyDescriptor descriptor)
        => FindRaw(descriptor.Name) is { } property && HasValue(property)
            ? Value<T>(descriptor, property)
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
        => FindRaw(descriptor.Name) is { } property && HasValue(property)
            ? Value<T>(descriptor, property)
            : descriptor.IsRequired ? throw Required(descriptor) : defaultValue;

    /// <summary>
    /// Returns false when the property is absent, its value is invalid or <typeparamref name="T"/> does not match the data type.
    /// Throws only when the value is a reference to a value that was not published (yet): an error in the schema, not an absent property.
    /// </summary>
    public bool TryGet<T>(PropertyDescriptor descriptor, out T value)
    {
        value = default!;
        try
        {
            if (FindRaw(descriptor.Name) is not { } property || !HasValue(property))
            {
                return false;
            }

            value = Value<T>(descriptor, property);
            return true;
        }
        catch (InvalidOperationException ex) when (ex is not SerializationException)
        {
            return false;
        }
    }

    // a reference has a value when it is published (with a non-null value); a constant when its text is not null
    private bool HasValue(SchemaProperty property)
        => IsReference(property) ? Resolve(property) is not null : property.Value is not null;

    private T Value<T>(PropertyDescriptor descriptor, SchemaProperty property)
    {
        var dataType = _dataTypes?.TryGet(descriptor.DataType, out var found) == true ? found
            : throw new InvalidOperationException(
                $"The data type '{descriptor.DataType}' of the '{FullName(descriptor.Name)}' property is not registered.");

        object? parsed;
        if (IsReference(property))
        {
            var resolved = Resolve(property);
            if (!dataType.TryConvert(resolved, out parsed))
            {
                throw new InvalidOperationException(
                    $"The '{FullName(descriptor.Name)}' value '{resolved}' (from '{_references![property.Name]}') cannot be converted to data type '{descriptor.DataType}'.");
            }
        }
        else if (!dataType.Parse(property.Value!, out parsed))
        {
            throw new InvalidOperationException($"Invalid '{FullName(descriptor.Name)}' value '{property.Value}' for data type '{descriptor.DataType}'.");
        }

        return parsed is T typed
            ? typed
            : throw new InvalidOperationException(
                $"The '{FullName(descriptor.Name)}' property is of type '{dataType.ClrType}', not '{typeof(T)}'.");
    }
}
