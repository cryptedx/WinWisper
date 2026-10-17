using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;

namespace WinWispr.Tests.Audio;

[TestClass]
public sealed class Pcm16Tests
{
    [TestMethod]
    public void Little_endian_samples_convert_to_normalized_floats()
    {
        byte[] pcm = [0x00, 0x80, 0x00, 0x00, 0xFF, 0x7F];

        float[] samples = Pcm16.ToFloat(pcm);

        Assert.AreEqual(3, samples.Length);
        Assert.AreEqual(-1f, samples[0]);
        Assert.AreEqual(0f, samples[1]);
        Assert.AreEqual(1f, samples[2]);
    }

    [TestMethod]
    public void A_trailing_odd_byte_is_ignored()
    {
        float[] samples = Pcm16.ToFloat([0xFF, 0x7F, 0x42]);

        CollectionAssert.AreEqual(new[] { 1f }, samples);
    }

    [TestMethod]
    public void Conversion_writes_into_a_caller_supplied_span()
    {
        var destination = new float[3];

        Pcm16.ToFloat([0xFF, 0x7F, 0x00, 0x80], destination);

        CollectionAssert.AreEqual(new[] { 1f, -1f, 0f }, destination);
    }

    [TestMethod]
    public void A_destination_that_is_too_small_is_rejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Pcm16.ToFloat([0x00, 0x00, 0x00, 0x00], new float[1]));
    }
}
