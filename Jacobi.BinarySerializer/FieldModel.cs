using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer;

// The flat value API: no groups, repeats or choices, just fields. The engine still walks all of them
// (processors, repeats, ...); the model is only asked for, or given, the values of the fields it visits.
// Which fields are visited is decided by the plan (optionally limited by a PlanRange), never by the model.
// The tree-based IValueSource / IValueSink extend these with scope navigation.

/// <summary>Provides the values to write, one field at a time.</summary>
public interface IFieldSource
{
    /// <summary>
    /// Gets the value of a field.
    /// Answer <see cref="SourceResult.NoValue"/> when the model has no value for the field; the engine then derives it (length, count, ...) or fails.
    /// Answer <see cref="SourceResult.EndOfData"/> on the first field of an item of a count-less repeat when there are no more items.
    /// </summary>
    SourceResult GetField(FieldContext context);
}

/// <summary>
/// The kind of answer a source gives when asked for a field value.
/// </summary>
public enum SourceStatus
{
    /// <summary>
    /// The model provided the value of the field.
    /// </summary>
    Value,
    /// <summary>
    /// The model has no value for the field.
    /// The engine derives it (length, count, size, constant, ...) or fails when it cannot.
    /// </summary>
    NoValue,
    /// <summary>
    /// The model has no more items for a repeat without a count.
    /// Only legal on the first field of an item of such a repeat, where it ends the repeat cleanly.
    /// Anywhere else (including repeats with a count) it is an error.
    /// </summary>
    EndOfData,
}

/// <summary>
/// The outcome of asking a source for a field value.
/// </summary>
public readonly record struct SourceResult(SourceStatus Status, LogicalField? Value)
{
    public static SourceResult Provided(LogicalField value) => new(SourceStatus.Value, value);
    public static SourceResult NoValue() => new(SourceStatus.NoValue, null);
    public static SourceResult EndOfData() => new(SourceStatus.EndOfData, null);
}

/// <summary>Receives the values that were read, one field at a time.</summary>
public interface IFieldSink
{
    /// <summary>Sets the value of a field. Fields the model does not map are ignored.</summary>
    void SetField(FieldContext context, LogicalField value);
}
