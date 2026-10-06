using System.Buffers;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Extracts values of arbitrary bit widths (1..64) from bytes, in the configured <see cref="Endianness"/>.
/// The reader only keeps the partially consumed byte; the byte source is passed in on each call.
/// </summary>
public sealed class BitReader
{
    private int _current;
    private int _bitsLeft;

    public BitReader(Endianness order = Endianness.Little)
    {
        Order = order;
    }

    public Endianness Order { get; }

    /// <summary>The number of unread bits left in the partially consumed byte (0 when byte aligned).</summary>
    public int PendingBits => _bitsLeft;

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits. Returns false (consuming nothing) when the reader does not have enough data yet.
    /// </summary>
    public bool TryRead(ref SequenceReader<byte> reader, int bitCount, out ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, 64);

        value = 0;
        var missingBits = bitCount - _bitsLeft;
        if (missingBits > 0 && reader.Remaining < (missingBits + 7) / 8)
        {
            return false;
        }

        var remaining = bitCount;
        var got = 0;
        while (remaining > 0)
        {
            if (_bitsLeft == 0)
            {
                reader.TryRead(out var next);
                _current = next;
                _bitsLeft = 8;
            }

            var take = Math.Min(_bitsLeft, remaining);
            if (Order == Endianness.Little)
            {
                value |= ((ulong)_current & BitWriter.Mask(take)) << got;
                _current >>= take;
            }
            else
            {
                var chunk = ((ulong)_current >> (_bitsLeft - take)) & BitWriter.Mask(take);
                value = take == 64 ? chunk : (value << take) | chunk;
            }

            got += take;
            _bitsLeft -= take;
            remaining -= take;
        }

        return true;
    }

    /// <summary>Discards the unread bits of the partially consumed byte (aligns to the next byte).</summary>
    public void Align() => _bitsLeft = 0;
}
