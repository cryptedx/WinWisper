namespace WinWispr.Speech;

/// <param name="ArchivePath">The member's path inside the archive, below the archive's single root folder.</param>
/// <param name="InstalledName">The file's path below the installation directory, with '/' between folders.</param>
internal sealed record ModelFileSpec(string ArchivePath, string InstalledName, long ExpectedBytes);

/// <summary>
/// One installable model set: where to download it, what the download must look like and which files the
/// installation must hold. <paramref name="SetDirectory"/> is the set's own folder in the app data, with its own
/// pointer, installations and staging area.
/// </summary>
internal sealed record ModelSpec(
    Uri Uri,
    long ArchiveBytes,
    string ArchiveSha256,
    IReadOnlyList<ModelFileSpec> Files,
    string SetDirectory = "Models");

/// <param name="RelativePath">The file's path below the set's folder, with '/' between folders.</param>
internal sealed record BundledModelFile(string RelativePath, long ExpectedBytes, string Sha256);

/// <summary>
/// A model set that ships as loose files in the app package and is read in place, from the folder
/// <paramref name="PackageDirectory"/> next to the executable. Nothing is downloaded or copied at run time.
/// </summary>
internal sealed record BundledModelSet(string PackageDirectory, IReadOnlyList<BundledModelFile> Files);

internal static class ModelManifest
{
    internal static readonly ModelSpec Parakeet = new(
        Uri: new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"),
        ArchiveBytes: 487_170_055,
        ArchiveSha256: "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf",
        Files:
        [
            new("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/encoder.int8.onnx", "encoder.int8.onnx", 652_184_281),
            new("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/decoder.int8.onnx", "decoder.int8.onnx", 11_845_275),
            new("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/joiner.int8.onnx", "joiner.int8.onnx", 6_355_277),
            new("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/tokens.txt", "tokens.txt", 93_939)
        ]);

    /// <summary>
    /// The translation model: both directions of Opus-MT (German to English and English to German) as ten int8 ONNX
    /// and tokenizer files. The WinWispr Models installer puts them in TranslationData next to the executable; Setup
    /// leaves an installed copy alone. They are the files that eng/translation-model-pins.json records with their
    /// provenance, and the model package build copies them only after checking every size and SHA-256;
    /// tests/translation-model-contract.sh checks that both lists agree.
    /// </summary>
    internal static readonly BundledModelSet Translation = new(
        PackageDirectory: "TranslationData",
        Files:
        [
            // de-en: Xenova/opus-mt-de-en, exported at commit 399dfd68706739fffd503f876093e455ae268a06
            new("opus-mt-de-en/onnx/encoder_model_quantized.onnx", 49_366_942, "4cedda8f8c89b72a42b3c6cd1e7a27f2de24457093e3bf80cb3e46829641fcd8"),
            new("opus-mt-de-en/onnx/decoder_model_merged_quantized.onnx", 56_652_404, "4712800584b2d77c2f9e620bf29523e2b20b62f65ade5011ee01780e2b0ea5d3"),
            new("opus-mt-de-en/source.spm", 796_845, "bbd1f495eea99c8e21ae086d9146e0fa7b096c3dfdd9ba07ab8b631889df5c9b"),
            new("opus-mt-de-en/target.spm", 768_489, "678f2a1177d8389f67b66299762dcc4fc567e89b07e212ba91b0c56daecf47ce"),
            new("opus-mt-de-en/vocab.json", 1_389_436, "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44"),
            // en-de: Xenova/opus-mt-en-de, exported at commit 1ca130c44c4c5441ef16d48aae521a424ab644f7
            new("opus-mt-en-de/onnx/encoder_model_quantized.onnx", 49_366_942, "15834b45fabd2dfb8c6c029b3ca3e7289aeefd90ece798ce42bcf548d1bd3b8d"),
            new("opus-mt-en-de/onnx/decoder_model_merged_quantized.onnx", 56_652_404, "8b46a825964cdd182fe47cc780f0fb87d357eae1f47cf20aa63c1c09be5b510c"),
            new("opus-mt-en-de/source.spm", 768_489, "678f2a1177d8389f67b66299762dcc4fc567e89b07e212ba91b0c56daecf47ce"),
            new("opus-mt-en-de/target.spm", 796_845, "bbd1f495eea99c8e21ae086d9146e0fa7b096c3dfdd9ba07ab8b631889df5c9b"),
            new("opus-mt-en-de/vocab.json", 1_389_436, "d5acea957b265a78554999144459c5e391e0df525864edc8287bc090290baa44")
        ]);
}
