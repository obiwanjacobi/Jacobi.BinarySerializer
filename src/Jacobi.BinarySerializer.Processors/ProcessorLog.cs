using Microsoft.Extensions.Logging;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Source-generated log messages of the built-in processors (the logger category identifies the processor).
/// </summary>
internal static partial class ProcessorLog
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Trace, Message = "Encoded '{Path}' as {Encoding} in {Bytes} byte(s).")]
    public static partial void VarIntEncoded(this ILogger logger, string path, string encoding, int bytes);

    [LoggerMessage(EventId = 201, Level = LogLevel.Trace, Message = "Decoded '{Path}' from {Encoding} ({Bytes} byte(s) provided).")]
    public static partial void VarIntDecoded(this ILogger logger, string path, string encoding, int bytes);

    [LoggerMessage(EventId = 210, Level = LogLevel.Debug, Message = "Padding {Count} byte(s) to align on {Alignment} at position {Position}.")]    public static partial void AlignPadded(this ILogger logger, int count, int alignment, long position);

    [LoggerMessage(EventId = 211, Level = LogLevel.Debug, Message = "Not enough data to skip {Count} alignment byte(s).")]
    public static partial void AlignNeedMoreData(this ILogger logger, long count);

    [LoggerMessage(EventId = 220, Level = LogLevel.Trace, Message = "Byte order of '{Path}' converted to {ByteOrder}.")]
    public static partial void ByteOrderConverted(this ILogger logger, string path, string byteOrder);

    [LoggerMessage(EventId = 230, Level = LogLevel.Trace, Message = "Packed {Bits} bit(s) of '{Path}'.")]
    public static partial void BitsPacked(this ILogger logger, string path, int bits);

    [LoggerMessage(EventId = 231, Level = LogLevel.Trace, Message = "Unpacked {Bits} bit(s) of '{Path}'.")]
    public static partial void BitsUnpacked(this ILogger logger, string path, int bits);

    [LoggerMessage(EventId = 232, Level = LogLevel.Warning, Message = "Cannot pack '{Path}': {Reason}.")]
    public static partial void BitsRejected(this ILogger logger, string path, string reason);

    [LoggerMessage(EventId = 240, Level = LogLevel.Trace, Message = "Mapped '{Path}' value '{From}' to '{To}'.")]
    public static partial void EnumMapped(this ILogger logger, string path, object? from, object? to);

    [LoggerMessage(EventId = 250, Level = LogLevel.Trace, Message = "Scaled '{Path}' by {Scale}: '{From}' to '{To}'.")]
    public static partial void Scaled(this ILogger logger, string path, decimal scale, object? from, object? to);

    [LoggerMessage(EventId = 290, Level = LogLevel.Error, Message = "{Message}")]
    private static partial void Failed(this ILogger logger, Exception? exception, string message);

    /// <summary>
    /// Logs the message as an error and returns the exception to throw (<c>throw context.Logger.Fail(...)</c>).
    /// </summary>
    public static InvalidOperationException Fail(this ILogger logger, string message, Exception? inner = null)
    {
        logger.Failed(inner, message);
        return new InvalidOperationException(message, inner);
    }
}
