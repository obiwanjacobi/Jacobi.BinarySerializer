using System.Text;

namespace Jacobi.BinarySerializer.IntegrationTests;

internal static class BinaryAssert
{
    private const int ContextBytes = 8;

    /// <summary>
    /// Asserts both byte sequences are equal. On mismatch reports the first differing offset and a hex dump around it.
    /// </summary>
    public static void AreEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var common = Math.Min(expected.Length, actual.Length);
        var offset = expected[..common].CommonPrefixLength(actual[..common]);

        if (offset == common && expected.Length == actual.Length)
            return;

        Assert.Fail(
            $"Binary mismatch at offset {offset} (0x{offset:X}). Expected length {expected.Length}, actual length {actual.Length}." +
            $"{Environment.NewLine}Expected: {Dump(expected, offset)}" +
            $"{Environment.NewLine}Actual:   {Dump(actual, offset)}");
    }

    /// <summary>
    /// Asserts that <paramref name="roundTrip"/> (read then write) reproduces the input exactly.
    /// </summary>
    public static void RoundTrips(byte[] input, Func<byte[], byte[]> roundTrip)
        => AreEqual(input, roundTrip(input));

    private static string Dump(ReadOnlySpan<byte> data, int offset)
    {
        var start = Math.Max(0, offset - ContextBytes);
        var end = Math.Min(data.Length, offset + ContextBytes);
        var sb = new StringBuilder();

        for (var i = start; i < end; i++)
        {
            if (i == offset) sb.Append('[');
            sb.Append(data[i].ToString("X2"));
            if (i == offset) sb.Append(']');
            sb.Append(' ');
        }

        if (offset >= data.Length) sb.Append("[end]");
        return $"@{start}: {sb.ToString().TrimEnd()}";
    }
}
