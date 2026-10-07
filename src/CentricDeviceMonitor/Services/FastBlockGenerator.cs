using System.Numerics;
using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Deterministic, high-throughput pseudo-random data generator used by the storage tests.
/// </summary>
/// <remarks>
/// The capacity test previously used <c>RandomNumberGenerator.Fill</c> for every block, which
/// tops out near 200 MB/s on typical hardware and therefore became the bottleneck instead of
/// the drive under test. This generator (SplitMix64 seeding into xoshiro256**) runs at several
/// GB/s and still produces incompressible, non-deduplicable data that is unique per block, so
/// counterfeit flash that silently wraps physical addresses is still detected.
///
/// Because output depends only on (seed, blockIndex), the verification pass can regenerate the
/// expected bytes on demand rather than storing a hash for every block.
/// </remarks>
internal static class FastBlockGenerator
{
    /// <summary>
    /// Fills <paramref name="destination"/> with data derived deterministically from
    /// <paramref name="seed"/> and <paramref name="blockIndex"/>.
    /// </summary>
    public static void Fill(Span<byte> destination, ulong seed, long blockIndex)
    {
        ulong state = seed ^ ((ulong)blockIndex * 0x9E3779B97F4A7C15UL);
        ulong s0 = SplitMix64(ref state);
        ulong s1 = SplitMix64(ref state);
        ulong s2 = SplitMix64(ref state);
        ulong s3 = SplitMix64(ref state);

        // Writing 64 bits at a time is what makes this fast; the tail below covers any
        // trailing bytes when the block length is not a multiple of eight.
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(destination);
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = Next(ref s0, ref s1, ref s2, ref s3);
        }

        int tailStart = words.Length * sizeof(ulong);
        int tailLength = destination.Length - tailStart;
        if (tailLength > 0)
        {
            Span<byte> tail = stackalloc byte[sizeof(ulong)];
            BitConverter.TryWriteBytes(tail, Next(ref s0, ref s1, ref s2, ref s3));
            tail[..tailLength].CopyTo(destination[tailStart..]);
        }
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Next(ref ulong s0, ref ulong s1, ref ulong s2, ref ulong s3)
    {
        ulong result = BitOperations.RotateLeft(s1 * 5, 7) * 9;
        ulong t = s1 << 17;

        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = BitOperations.RotateLeft(s3, 45);

        return result;
    }
}
