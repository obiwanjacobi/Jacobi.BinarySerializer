using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer;

/// <summary>
/// Whether the <see cref="Serializer"/> was reading or writing when a <see cref="SerializationException"/> was raised.
/// </summary>
public enum SerializationDirection
{
    Read,
    Write,
}

/// <summary>
/// The categories of a <see cref="SerializationException"/> the engine reports. A processor may report a category of
/// its own (any string, e.g. <c>checksum</c>): the category is not limited to these values.
/// </summary>
public static class SerializationCategories
{
    /// <summary>A field does not have the value the schema requires (a <c>value</c> constant or reference).</summary>
    public const string ExpectedValue = "expectedValue";
    /// <summary>Read: the bytes do not satisfy the schema. The default of a read failure.</summary>
    public const string InvalidData = "invalidData";
    /// <summary>Write: a value of the model cannot be written. The default of a write failure.</summary>
    public const string InvalidValue = "invalidValue";
    /// <summary>Read: the input ended before the node was complete.</summary>
    public const string EndOfData = "endOfData";
    /// <summary>The schema, the plan or the value model cannot be used as it is (not the data).</summary>
    public const string Schema = "schema";

    /// <summary>The category of a failure that does not give one: <see cref="InvalidData"/> or <see cref="InvalidValue"/>.</summary>
    public static string DefaultOf(SerializationDirection direction)
        => direction == SerializationDirection.Read ? InvalidData : InvalidValue;
}

/// <summary>
/// A read or write of the <see cref="Serializer"/> that failed: what went wrong (<see cref="Category"/>), who reported
/// it (<see cref="Origin"/>: the engine or a processor), where (<see cref="Path"/>, <see cref="Instance"/>,
/// <see cref="Position"/>) and, where it applies, the value that was expected and the one that was found.
/// The engine fills in the location of every failure of a read or write, also of the failures processors report.
/// </summary>
public sealed class SerializationException : InvalidOperationException
{
    /// <summary>The <see cref="Origin"/> of a failure the engine reports itself.</summary>
    public const string EngineOrigin = "engine";

    private readonly string? _category;

    internal SerializationException(string reason, string? category, string origin, object? expected, object? actual, Exception? inner)
        : base(reason, inner)
    {
        Reason = reason;
        _category = category;
        Origin = origin;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>What went wrong, without the location (<see cref="Message"/> adds it).</summary>
    public string Reason { get; }

    /// <summary>Reading or writing.</summary>
    public SerializationDirection Direction { get; private set; }

    /// <summary>
    /// What kind of failure it is: one of <see cref="SerializationCategories"/> or a processor's own category; the
    /// default of the <see cref="Direction"/> when the reporter gave none.
    /// </summary>
    public string Category => _category ?? SerializationCategories.DefaultOf(Direction);

    /// <summary>Who reported it: <see cref="EngineOrigin"/>, or the key of a processor (e.g. <c>sys.crc</c>).</summary>
    public string Origin { get; }

    /// <summary>The node that was read or written; null when the failure happened outside a read or write.</summary>
    public SchemaPath? Path { get; private set; }

    /// <summary>The repeat items that lead to the node (empty outside repeats).</summary>
    public InstancePath Instance { get; private set; }

    /// <summary>
    /// The byte position of the step that failed (where a field starts, or a group starts or ends): in the input
    /// (read) or the output (write), after the stream stage (e.g. framing) when the root has stream processors. Null
    /// when it is not known (e.g. in the stream stage).
    /// </summary>
    public long? Position { get; private set; }

    /// <summary>The value that was expected, where it applies (e.g. a constant, a stored checksum).</summary>
    public object? Expected { get; }

    /// <summary>The value that was found, where it applies.</summary>
    public object? Actual { get; }

    /// <summary>The <see cref="Reason"/> with the location and the reporter.</summary>
    public override string Message
    {
        get
        {
            if (Path is not { } path)
            {
                return Reason;
            }
            var node = Instance.Length > 0 ? $"{path}{Instance}" : path.ToString();
            // the reason often names the node already
            var where = Reason.Contains($"'{path}'", StringComparison.Ordinal) ? String.Empty : $" '{node}'";
            var position = Position is { } at ? $" at byte {at}" : String.Empty;
            return $"{Reason} ({(Direction == SerializationDirection.Read ? "read" : "write")}{where}{position}; {Origin}: {Category})";
        }
    }

    // the location is set once: by the session that runs the read or write in which the failure happened
    internal void Locate(SerializationDirection direction, SchemaPath? path, InstancePath instance, long? position)
    {
        if (Path is not null)
        {
            return;
        }
        Direction = direction;
        Path = path;
        Instance = instance;
        Position = position;
    }
}
