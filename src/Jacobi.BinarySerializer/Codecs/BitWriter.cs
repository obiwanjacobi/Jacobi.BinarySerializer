using System.Buffers;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Packs values of arbitrary bit widths (1..64) into bytes, in the configured <see cref="Endianness"/>.
/// Call <see cref="Flush"/> to pad a partially filled byte (with zero bits) and write it out.
/// </summary>
public sealed class BitWriter
{
    private readonly IBufferWriter<byte> _target;
    private int _current;
    private int _count;

    public BitWriter(IBufferWriter<byte> target, Endianness order = Endianness.Little)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        Order = order;
    }

    public Endianness Order { get; }

    /// <summary>The number of bits in the partially filled byte (0 when byte aligned).</summary>
    public int PendingBits => _count;

    /// <summary>Writes the lowest <paramref name="bitCount"/> bits of <paramref name="value"/>.</summary>
    public void Write(ulong value, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, 64);

        var remaining = bitCount;
        while (remaining > 0)
        {
            var take = Math.Min(8 - _count, remaining);
            if (Order == Endianness.Little)
            {
                _current |= (int)(value & Mask(take)) << _count;
                value >>= take;
            }
            else
            {
                var chunk = (int)((value >> (remaining - take)) & Mask(take));
                _current |= chunk << (8 - _count - take);
            }

            _count += take;
            remaining -= take;
            if (_count == 8)
            {
                Emit();
            }
        }
    }

    /// <summary>Pads the partially filled byte with zero bits and writes it. Does nothing when byte aligned.</summary>
    public void Flush()
    {
        if (_count > 0)
        {
            Emit();
        }
    }

    private void Emit()
    {
        _target.GetSpan(1)[0] = (byte)_current;
        _target.Advance(1);
        _current = 0;
        _count = 0;
    }

    /// <summary>A mask with the lowest <paramref name="bits"/> bits set.</summary>
    public static ulong Mask(int bits)
        => bits >= 64 ? UInt64.MaxValue : (1UL << bits) - 1;
}
