using System.Collections;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class EnumProcessor : IValueProcessor
{
    // each property represents a valid enumeration value and its corresponding integer representation

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        var map = new EnumForwardMap(context.ProcessorProperties);
        if (!map.Contains(logicalValue.Value?.ToString() ?? String.Empty))
        {
            throw new InvalidOperationException($"Value '{logicalValue.Value}' is not a valid enumeration option.");
        }

        var value = map[logicalValue.Value?.ToString() ?? String.Empty].FirstOrDefault();

        return new(logicalValue.Name, typeof(int), value);
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (Int32.TryParse(logicalValue.Value?.ToString(), out var intValue))
        {
            var map = new EnumReverseMap(context.ProcessorProperties);
            if (!map.Contains(intValue))
            {
                throw new InvalidOperationException($"Value '{intValue}' ({logicalValue.Value}) is not a valid enumeration value.");
            }

            var value = map[intValue].FirstOrDefault();

            return new(logicalValue.Name, typeof(string), value);
        }

        return logicalValue;
    }

    public ProcessorKey Key => new("sys:enum");
    public string Name => "Enumeration Processor";
    public PipelineStage Stage => PipelineStage.Semantic;

    public IReadOnlyList<PropertyDescriptor> Properties => [];

    private sealed class EnumForwardMap : ILookup<string, int>
    {
        private readonly Dictionary<string, int> _map = new();

        public EnumForwardMap(IEnumerable<SchemaProperty> properties)
        {
            foreach (var property in properties)
            {
                if (!_map.ContainsKey(property.Name))
                {
                    if (Int32.TryParse(property.Value?.ToString(), out var intValue))
                    {
                        _map.Add(property.Name, intValue);
                    }
                }
            }
        }

        public bool Contains(string key)
            => _map.ContainsKey(key);

        public int Count => _map.Count;

        public IEnumerable<int> this[string key]
            => _map.TryGetValue(key, out var value)
                ? [value]
                : Array.Empty<int>();

        public IEnumerator<IGrouping<string, int>> GetEnumerator()
            => _map.GroupBy(kvp => kvp.Key, kvp => kvp.Value).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => _map.GetEnumerator();
    }

    private sealed class EnumReverseMap : ILookup<int, string>
    {
        private readonly Dictionary<int, string> _map = new();

        public EnumReverseMap(IEnumerable<SchemaProperty> properties)
        {
            foreach (var property in properties)
            {
                if (Int32.TryParse(property.Value?.ToString(), out var intValue))
                {
                    if (!_map.ContainsKey(intValue))
                    {
                        _map.Add(intValue, property.Name);
                    }
                }
            }
        }

        public bool Contains(int key)
            => _map.ContainsKey(key);

        public int Count => _map.Count;

        public IEnumerable<string> this[int key]
            => _map.TryGetValue(key, out var value)
                ? [value]
                : Array.Empty<string>();

        public IEnumerator<IGrouping<int, string>> GetEnumerator()
            => _map.GroupBy(kvp => kvp.Key, kvp => kvp.Value).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => _map.GetEnumerator();
    }
}