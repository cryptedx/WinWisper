using System.Buffers.Binary;
using System.Text;

namespace WinWispr.Tests.Speech;

/// <summary>Reads mono 16-bit PCM WAV files as 16 kHz float samples (linear resampling) for speech fixtures.</summary>
internal static class Pcm16Wave
{
    /// <summary>Raw little-endian PCM16, the format of WINWISPR_E2E_AUDIO_PCM16.</summary>
    public static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int index = 0; index < samples.Length; index++)
        {
            short value = (short)Math.Clamp(MathF.Round(samples[index] * 32_768f), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2, 2), value);
        }

        return bytes;
    }

    public static float[] ReadMono16Khz(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12
            || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Expected a RIFF/WAVE file.");
        }

        ushort audioFormat = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;
        int offset = 12;

        while (offset + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkLength < 0 || offset + 8 + chunkLength > bytes.Length)
                throw new InvalidDataException("The WAVE chunk is truncated.");

            int chunkData = offset + 8;
            if (chunkId == "fmt " && chunkLength >= 16)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkData + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkData + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkData;
                dataLength = chunkLength;
            }

            offset = chunkData + chunkLength + (chunkLength & 1);
        }

        if (audioFormat != 1 || channels != 1 || sampleRate == 0 || bitsPerSample != 16)
            throw new InvalidDataException("Only mono 16-bit PCM is supported.");
        if (dataOffset < 0 || dataLength % 2 != 0)
            throw new InvalidDataException("The WAVE file has no valid PCM data chunk.");

        float[] source = new float[dataLength / 2];
        for (int index = 0; index < source.Length; index++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(dataOffset + index * 2, 2));
            source[index] = sample / 32_768f;
        }

        if (sampleRate == 16_000)
            return source;

        int outputLength = (int)Math.Round(source.Length * 16_000d / sampleRate);
        float[] samples = new float[outputLength];
        for (int index = 0; index < samples.Length; index++)
        {
            double sourcePosition = index * sampleRate / 16_000d;
            int left = (int)sourcePosition;
            int right = Math.Min(left + 1, source.Length - 1);
            float fraction = (float)(sourcePosition - left);
            samples[index] = source[left] + (source[right] - source[left]) * fraction;
        }

        return samples;
    }
}
