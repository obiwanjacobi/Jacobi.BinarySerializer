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
    /// Returns false when the model has no value for the field; the engine then derives it (length, count, ...) or fails.
    /// </summary>
    bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? value);
}

/// <summary>Receives the values that were read, one field at a time.</summary>
public interface IFieldSink
{
    /// <summary>Sets the value of a field. Fields the model does not map are ignored.</summary>
    void SetField(FieldContext context, LogicalField value);
}
