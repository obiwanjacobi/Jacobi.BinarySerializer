using System.Collections;
using Jacobi.BinarySerializer.Processor;
using Microsoft.Extensions.Logging;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Decorates the logger of a processor binding: every entry that is actually written is wrapped in a scope
/// that describes the current processor, stage, schema node and instance (read from the live context).
/// </summary>
/// <remarks>
/// The message is not touched, so structured logging stays intact. Nothing is allocated for disabled levels.
/// </remarks>
internal sealed class ContextLogger(ILogger inner, ProcessorContext context, ProcessorBinding binding, string direction) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel))
        {
            return;
        }

        using (inner.BeginScope(new ProcessorScope(binding, context, direction)))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>
/// The scope state: a read-only list of key/value pairs that logging providers turn into structured properties.
/// </summary>
internal readonly struct ProcessorScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly ProcessorBinding _binding;
    private readonly string _stage;
    private readonly string _path;
    private readonly InstancePath _instance;
    private readonly string _direction;
    private readonly long? _rootPosition;
    private readonly long? _groupPosition;

    public ProcessorScope(ProcessorBinding binding, ProcessorContext context, string direction)
    {
        _binding = binding;
        _stage = context.Stage.ToString();
        _path = context.NodePath;
        _instance = context.Instance;
        _direction = direction;
        _rootPosition = context.ScopeRootPosition;
        _groupPosition = context.ScopeGroupPosition;
    }

    public int Count => _rootPosition is null ? 6 : 8;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("Processor", _binding.ProcessorType),
        1 => new("ProcessorKey", _binding.Processor.Key.ToString()),
        2 => new("Stage", _stage),
        3 => new("Path", _path),
        4 => new("Instance", _instance.ToString()),
        5 => new("Direction", _direction),
        6 when _rootPosition is not null => new("RootPosition", _rootPosition),
        7 when _groupPosition is not null => new("GroupPosition", _groupPosition),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
        => $"{_binding.ProcessorType} {_binding.Processor.Key} {_path} {_instance}".TrimEnd();
}
