using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using Microsoft.Extensions.Logging;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class LoggingTests
{
    private const string EngineCategory = "Jacobi.BinarySerializer.Engine";
    private const string VarIntCategory = "sys.varint";

    private sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Scope);

    private sealed class CaptureProvider : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];
        public Stack<object> Scopes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, this);
        public void Dispose() { }
    }

    private sealed class PopScope(Stack<object> scopes) : IDisposable
    {
        public void Dispose() => scopes.Pop();
    }

    private sealed class CaptureLogger(string category, CaptureProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            provider.Scopes.Push(state);
            return new PopScope(provider.Scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            foreach (var item in provider.Scopes.Reverse().OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany(s => s))
            {
                scope[item.Key] = item.Value;
            }
            provider.Entries.Add(new Entry(category, logLevel, formatter(state, exception), scope));
        }
    }

    private sealed class Services(ILoggerFactory factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoggerFactory) ? factory : null;
    }

    private static List<Entry> ReadWithLogging(Action<ILoggingBuilder> configure)
    {
        var capture = new CaptureProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            configure(builder);
        });

        var root = Group("Root", [],
            Field("A", "UInt32", [Ref("varint")]));

        var result = new ReaderSession(Build(root), new Services(factory))
            .Read(new ReadOnlySequence<byte>(new byte[] { 0xAC, 0x02 }), new DictSink());
        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));

        return capture.Entries;
    }

    [Test]
    public void Read_WithoutLoggerFactory_DoesNotThrow()
    {
        var root = Group("Root", [],
            Field("A", "UInt32", [Ref("varint")]));

        Assert.DoesNotThrow(() => new ReaderSession(Build(root))
            .Read(new ReadOnlySequence<byte>(new byte[] { 0xAC, 0x02 }), new DictSink()));
    }

    [Test]
    public void Read_ProcessorLogsUnderItsOwnCategory()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Trace));

        Assert.That(entries.Any(e => e.Category == VarIntCategory && e.Level == LogLevel.Trace), Is.True);
    }

    [Test]
    public void Read_EngineLogsUnderEngineCategory()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        var engine = entries.Where(e => e.Category == EngineCategory).ToList();
        Assert.That(engine.Select(e => e.Message), Has.Some.Contain("Read started"));
        Assert.That(engine.Select(e => e.Message), Has.Some.Contain("Read finished"));
    }

    [Test]
    public void Read_MinimumLevelInformation_FiltersDebugAndTrace()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Information));

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public void Read_MinimumLevelDebug_FiltersTrace()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.Select(e => e.Level), Has.None.EqualTo(LogLevel.Trace));
    }

    [Test]
    public void Read_ProcessorEntry_CarriesContextScope()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Trace));

        var entry = entries.First(e => e.Category == VarIntCategory);
        Assert.That(entry.Scope["Processor"], Is.EqualTo("VarIntProcessor"));
        Assert.That(entry.Scope["ProcessorKey"], Is.EqualTo("sys.varint"));
        Assert.That(entry.Scope["Stage"], Is.EqualTo("Representation"));
        Assert.That(entry.Scope["Path"]?.ToString(), Does.EndWith("A"));
        Assert.That(entry.Scope["Direction"], Is.EqualTo("Read"));
    }

    [Test]
    public void Read_EngineEntry_HasNoProcessorScope()
    {
        var entries = ReadWithLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        Assert.That(entries.Where(e => e.Category == EngineCategory).Select(e => e.Scope), Has.All.Empty);
    }

    [Test]
    public void Read_ProcessorThrows_LogsErrorUnderProcessorCategoryFirst()
    {
        var capture = new CaptureProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Error));
        var root = Group("Root", [],
            Field("A", "UInt32", [Ref("align")]));

        // 'align' has no required 'bytes' property: the processor throws.
        Assert.Throws<InvalidOperationException>(() => new ReaderSession(Build(root), new Services(factory))
            .Read(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }), new DictSink()));

        Assert.That(capture.Entries, Has.Count.EqualTo(1));
        Assert.That(capture.Entries[0].Level, Is.EqualTo(LogLevel.Error));
        Assert.That(capture.Entries[0].Category, Is.EqualTo("sys.align"));
    }

    [Test]
    public void Read_FilterOnProcessorCategory_HidesOnlyThatCategory()
    {
        var entries = ReadWithLogging(b => b
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter(VarIntCategory, LogLevel.None));

        Assert.That(entries.Select(e => e.Category), Has.None.EqualTo(VarIntCategory));
        Assert.That(entries.Select(e => e.Category), Has.Some.EqualTo(EngineCategory));
    }

    [Test]
    public void Read_FilterOnEngineCategory_HidesOnlyTheEngine()
    {
        var entries = ReadWithLogging(b => b
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter(EngineCategory, LogLevel.None));

        Assert.That(entries.Select(e => e.Category), Has.None.EqualTo(EngineCategory));
        Assert.That(entries.Select(e => e.Category), Has.Some.EqualTo(VarIntCategory));
    }
}
