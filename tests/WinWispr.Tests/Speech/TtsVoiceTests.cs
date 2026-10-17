using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class TtsVoiceTests
{
    [TestMethod]
    public void Manifest_rejects_corruption_unlisted_files_and_path_escape()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinWispr-voice-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            string[] paths = ["model.onnx", "tokens.txt", "LICENSE", "data/phonemes"];
            foreach (string path in paths) File.WriteAllText(Path.Combine(root, path), "test data");
            var files = paths.Select(path => new TtsVoiceFile(path, new FileInfo(Path.Combine(root, path)).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))))).ToArray();
            File.WriteAllText(Path.Combine(root, "voice.json"), JsonSerializer.Serialize(
                new TtsVoice("test", "de-DE", "LICENSE", "model.onnx", "tokens.txt", "data", files)));
            Assert.AreEqual("test", TtsVoice.Read(root).Id);
            File.WriteAllText(Path.Combine(root, "extra"), "unexpected");
            Assert.ThrowsExactly<InvalidDataException>(() => TtsVoice.Read(root));
            File.Delete(Path.Combine(root, "extra"));
            File.WriteAllText(Path.Combine(root, "model.onnx"), "bad bytes");
            Assert.ThrowsExactly<InvalidDataException>(() => TtsVoice.Read(root));
            foreach (string path in new[] { "../outside", "/absolute", "C:/drive", "data\\file", "data/../file" })
                Assert.ThrowsExactly<InvalidDataException>(() => TtsVoice.Resolve(root, path));
        }
        finally { Directory.Delete(root, true); }
    }
}
