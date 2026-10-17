using System.Buffers.Binary;

namespace WinWispr.Audio;

/// <summary>Converts little-endian 16-bit PCM into the normalized float samples the capture pipeline uses.</summary>
internal static class Pcm16
{
    /// <summary>Converts every complete sample of <paramref name="pcm"/>; a trailing odd byte is ignored.</summary>
    internal static void ToFloat(ReadOnlySpan<byte> pcm, Span<float> samples)
    {
        int count = pcm.Length / sizeof(short);
        if (samples.Length < count)
            throw new ArgumentException("The destination is too small for the PCM data.", nameof(samples));

        for (int index = 0; index < count; index++)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * sizeof(short), sizeof(short)));
            samples[index] = Math.Max(-1f, value / (float)short.MaxValue);
        }
    }

    internal static float[] ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / sizeof(short)];
        ToFloat(pcm, samples);
        return samples;
    }
}
