using Jacobi.BinarySerializer.Processor;
using Microsoft.Extensions.Logging;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Source-generated log messages of the engine (category 'Jacobi.BinarySerializer.Engine').
/// </summary>
internal static partial class EngineLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Debug, Message = "Write started: '{Path}'.")]
    public static partial void WriteStarted(this ILogger logger, string path);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug, Message = "Write finished: '{Path}'.")]
    public static partial void WriteFinished(this ILogger logger, string path);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Write of '{Path}' stopped with {Result}.")]
    public static partial void WriteStopped(this ILogger logger, string path, WriteResult result);

    [LoggerMessage(EventId = 103, Level = LogLevel.Trace, Message = "Writing field '{Path}' at instance {Instance}.")]
    public static partial void WritingField(this ILogger logger, string path, string instance);

    [LoggerMessage(EventId = 110, Level = LogLevel.Debug, Message = "Read started: '{Path}'.")]
    public static partial void ReadStarted(this ILogger logger, string path);

    [LoggerMessage(EventId = 111, Level = LogLevel.Debug, Message = "Read finished: '{Path}'.")]
    public static partial void ReadFinished(this ILogger logger, string path);

    [LoggerMessage(EventId = 112, Level = LogLevel.Debug, Message = "Read of '{Path}' stopped with {Result}.")]
    public static partial void ReadStopped(this ILogger logger, string path, ReadResult result);

    [LoggerMessage(EventId = 113, Level = LogLevel.Trace, Message = "Reading field '{Path}' at instance {Instance}.")]
    public static partial void ReadingField(this ILogger logger, string path, string instance);

    [LoggerMessage(EventId = 120, Level = LogLevel.Debug, Message = "Repeat '{Path}' has {Count} items.")]
    public static partial void RepeatCount(this ILogger logger, string path, int count);

    [LoggerMessage(EventId = 121, Level = LogLevel.Debug, Message = "Choice '{Path}' selected option {Index}.")]
    public static partial void ChoiceSelected(this ILogger logger, string path, int index);

    [LoggerMessage(EventId = 190, Level = LogLevel.Error, Message = "{Message}")]
    private static partial void Failed(this ILogger logger, Exception? exception, string message);

    /// <summary>
    /// Logs the message as an error and returns the exception to throw (<c>throw logger.Fail(...)</c>).
    /// </summary>
    public static InvalidOperationException Fail(this ILogger logger, string message, Exception? inner = null)
    {
        logger.Failed(inner, message);
        return new InvalidOperationException(message, inner);
    }
}
