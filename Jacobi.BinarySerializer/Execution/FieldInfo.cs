namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Represents all the information available for a field.
/// </summary>
public sealed class FieldInfo
{
    // schema parent
    // schema field
    // codecs (objects implementing ICodec)

    // parent GroupInfo
    // previous FieldInfo
    // next FieldInfo
}


public sealed class GroupInfo
{
    // schema parent
    // schema group
    // codecs (objects implementing ICodec)

    // parent GroupInfo
    // previous GroupInfo
    // next GroupInfo
}