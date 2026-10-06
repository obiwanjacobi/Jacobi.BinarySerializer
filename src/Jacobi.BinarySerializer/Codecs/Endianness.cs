namespace Jacobi.BinarySerializer.Codecs;

/// <summary>Which part of a value comes first in a stream: the least significant (little) or the most significant (big).</summary>
/// <remarks>
/// For a bit stream this is also the bit fill order: <see cref="Little"/> fills each byte from its least significant bit,
/// <see cref="Big"/> from its most significant bit.
/// </remarks>
public enum Endianness
{
    /// <summary>Least significant part first.</summary>
    Little,
    /// <summary>Most significant part first.</summary>
    Big,
}
