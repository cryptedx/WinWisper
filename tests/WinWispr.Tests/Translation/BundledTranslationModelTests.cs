using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

[TestClass]
public sealed class BundledTranslationModelTests
{
    private static readonly BundledModelSet SmallSet = new(
        PackageDirectory: "TranslationData",
        Files:
        [
            new("opus-mt-de-en/onnx/encoder_model_quantized.onnx", 3, new string('0', 64)),
            new("opus-mt-de-en/source.spm", 2, new string('0', 64)),
            new("opus-mt-en-de/onnx/encoder_model_quantized.onnx", 3, new string('0', 64)),
            new("opus-mt-en-de/source.spm", 2, new string('0', 64))
        ]);

    [TestMethod]
    public void Translation_manifest_lists_the_ten_qualified_files_of_both_directions()
    {
        BundledModelSet translation = ModelManifest.Translation;

        Assert.AreEqual("TranslationData", translation.PackageDirectory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "opus-mt-de-en/onnx/encoder_model_quantized.onnx",
                "opus-mt-de-en/onnx/decoder_model_merged_quantized.onnx",
                "opus-mt-de-en/source.spm",
                "opus-mt-de-en/target.spm",
                "opus-mt-de-en/vocab.json",
                "opus-mt-en-de/onnx/encoder_model_quantized.onnx",
                "opus-mt-en-de/onnx/decoder_model_merged_quantized.onnx",
                "opus-mt-en-de/source.spm",
                "opus-mt-en-de/target.spm",
                "opus-mt-en-de/vocab.json"
            },
            translation.Files.Select(file => file.RelativePath).ToArray());
        BundledModelFile germanEncoder = translation.Files.Single(file => file.RelativePath == "opus-mt-de-en/onnx/encoder_model_quantized.onnx");
        Assert.AreEqual(49_366_942L, germanEncoder.ExpectedBytes);
        Assert.AreEqual("4cedda8f8c89b72a42b3c6cd1e7a27f2de24457093e3bf80cb3e46829641fcd8", germanEncoder.Sha256);
        Assert.IsTrue(translation.Files.All(file => file.Sha256.Length == 64 && file.Sha256 == file.Sha256.ToLowerInvariant()));
        Assert.AreEqual(217_948_232L, translation.Files.Sum(file => file.ExpectedBytes));
    }

    [TestMethod]
    public async Task A_complete_set_is_installed_and_names_its_folder()
    {
        using var root = new TemporaryDirectory();
        await WriteSetAsync(root);
        var model = new BundledTranslationModel(Path.Combine(root.Path, "TranslationData"), SmallSet);

        Assert.AreEqual(Path.Combine(root.Path, "TranslationData"), model.InstalledDirectory);
        Assert.IsTrue(model.IsInstalled);
        Assert.AreEqual(TranslationModelState.Installed, model.State);
    }

    [TestMethod]
    public void A_missing_folder_is_missing()
    {
        using var root = new TemporaryDirectory();
        var model = new BundledTranslationModel(Path.Combine(root.Path, "TranslationData"), SmallSet);

        Assert.IsNull(model.InstalledDirectory);
        Assert.AreEqual(TranslationModelState.Missing, model.State);
    }

    [TestMethod]
    [DataRow("opus-mt-de-en/source.spm")]
    [DataRow("opus-mt-en-de/onnx/encoder_model_quantized.onnx")]
    public async Task A_lost_file_of_either_direction_makes_the_model_missing(string lostFile)
    {
        using var root = new TemporaryDirectory();
        await WriteSetAsync(root);
        File.Delete(Path.Combine(root.Path, "TranslationData", lostFile.Replace('/', Path.DirectorySeparatorChar)));
        var model = new BundledTranslationModel(Path.Combine(root.Path, "TranslationData"), SmallSet);

        Assert.AreEqual(TranslationModelState.Missing, model.State);
    }

    [TestMethod]
    [DataRow("x")]
    [DataRow("xxxx")]
    public async Task A_file_of_the_wrong_size_makes_the_model_missing(string contents)
    {
        using var root = new TemporaryDirectory();
        await WriteSetAsync(root);
        await root.WriteTextAsync("TranslationData/opus-mt-de-en/onnx/encoder_model_quantized.onnx", contents);
        var model = new BundledTranslationModel(Path.Combine(root.Path, "TranslationData"), SmallSet);

        Assert.AreEqual(TranslationModelState.Missing, model.State);
    }

    [TestMethod]
    public async Task The_state_follows_the_files_without_a_new_instance()
    {
        using var root = new TemporaryDirectory();
        var model = new BundledTranslationModel(Path.Combine(root.Path, "TranslationData"), SmallSet);
        Assert.AreEqual(TranslationModelState.Missing, model.State);

        await WriteSetAsync(root);

        Assert.AreEqual(TranslationModelState.Installed, model.State);
    }

    [TestMethod]
    public async Task Removing_the_downloaded_copy_deletes_only_the_old_translation_folder()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("TranslationModels/installs/abc/opus-mt-de-en/source.spm", "old download");
        await root.WriteTextAsync("TranslationModels/active.json", "{}");
        await root.WriteTextAsync("Models/active.json", "{}");
        await root.WriteTextAsync("settings.json", "{}");

        Assert.IsTrue(BundledTranslationModel.RemoveDownloadedCopy(root.Path));

        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "TranslationModels")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "Models", "active.json")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "settings.json")));
    }

    [TestMethod]
    public void Removing_the_downloaded_copy_is_harmless_when_there_is_none()
    {
        using var root = new TemporaryDirectory();

        Assert.IsTrue(BundledTranslationModel.RemoveDownloadedCopy(root.Path));
    }

    [TestMethod]
    public async Task A_downloaded_copy_in_use_stays_and_a_later_start_tries_again()
    {
        using var root = new TemporaryDirectory();
        await root.WriteTextAsync("TranslationModels/installs/abc/opus-mt-de-en/source.spm", "old download");
        string inUse = Path.Combine(root.Path, "TranslationModels", "installs", "abc", "opus-mt-de-en", "source.spm");

        using (new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.IsFalse(BundledTranslationModel.RemoveDownloadedCopy(root.Path));

        Assert.IsTrue(BundledTranslationModel.RemoveDownloadedCopy(root.Path));
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "TranslationModels")));
    }

    private static async Task WriteSetAsync(TemporaryDirectory root)
    {
        foreach (BundledModelFile file in SmallSet.Files)
            await root.WriteTextAsync("TranslationData/" + file.RelativePath, new string('m', (int)file.ExpectedBytes));
    }
}
