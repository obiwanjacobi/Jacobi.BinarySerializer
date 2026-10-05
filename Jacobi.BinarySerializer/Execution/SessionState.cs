using System.Runtime.InteropServices;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Maintains the state of a processing session, including the pipeline and context.
/// </summary>
public closed class SessionState
{
    // private processor state
    private readonly record struct PrivateKey(ProcessorBinding Owner, Type StateType, InstancePath Instance);
    private readonly Dictionary<PrivateKey, object> _private = [];

    internal T GetOrCreate<T>(ProcessorBinding owner, InstancePath instance = default) where T : class, new()
        => GetOrCreate<T>(owner, instance, out _);

    internal T GetOrCreate<T>(ProcessorBinding owner, InstancePath instance, out bool exists) where T : class, new()
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_private, new PrivateKey(owner, typeof(T), instance), out exists);
        return (T)(slot ??= new T());
    }

    // shared processor state
    private readonly Dictionary<PublishedValueKey, object?> _published = [];

    /// <summary>
    /// Publishes a public value (e.g. to prefill a repeat count before writing); a later publication of the same key overwrites the earlier one.
    /// </summary>
    public void Publish(PublishedValueKey key, object? value) => _published[key] = value;

    /// <summary>Publishes a public value by namespace and name ('pubns/name').</summary>
    public void Publish(string ns, string name, object? value) => Publish(new PublishedValueKey(ns, name), value);

    /// <summary>Publishes the value of the field at a schema path.</summary>
    public void Publish(SchemaPath path, object? value) => Publish(PublishedValueKey.ForPath(path), value);

    /// <summary>Publishes the value of the field at a schema path for one instance of its repeats.</summary>
    public void Publish(SchemaPath path, InstancePath instance, object? value) => Publish(PublishedValueKey.ForPath(path, instance), value);

    /// <summary>Resolves a constant or a published value to an int. An unpublished value is a runtime error.</summary>
    /// <param name="current">The instance of the referring node; replaces the '[]' markers of the reference.</param>
    internal int Resolve(ValueSource<int> source, SchemaPath referrer, InstancePath current = default)
    {
        if (source is int constant)
        {
            return constant;
        }

        if (source is PublishedValueKey declared)
        {
            var key = declared with { Instance = declared.Instance.ResolveRelative(current) };
            if (!_published.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"'{referrer}': the value '{key}' was not published (yet).");
            }

            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidOperationException($"'{referrer}': the value '{key}' ({value ?? "null"}) is not an integer.", ex);
            }
        }

        throw new InvalidOperationException($"'{referrer}': the value source is unresolved.");
    }
    // buffer state/management
}
