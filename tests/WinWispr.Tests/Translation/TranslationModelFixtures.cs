using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The translation model files for acceptance tests. WINWISPR_TRANSLATION_MODEL_DIR names the folder that holds
/// <c>opus-mt-de-en</c> and <c>opus-mt-en-de</c> (the layout of the TranslationData folder in the package). The tests are
/// inconclusive when the variable is missing and the agreed folder of the Windows build host does not exist. When
/// a folder is found, every pinned file must be there with the pinned size
/// and SHA-256, so an acceptance result always describes the exact qualified model.
/// </summary>
internal static class TranslationModelFixtures
{
    internal const string Variable = "WINWISPR_TRANSLATION_MODEL_DIR";

    private static readonly Lazy<string> Verified = new(Verify);

    /// <summary>The verified model folder, or Inconclusive when the variable is not set.</summary>
    internal static string Require()
    {
        if (ConfiguredDirectory() is null)
            Assert.Inconclusive(
                $"Set {Variable} to the folder that holds opus-mt-de-en and opus-mt-en-de (the ten pinned model files). "
                + "Without the real model this is not acceptance evidence.");
        return Verified.Value;
    }

    /// <summary>
    /// The folder named by the variable, else the agreed place on the Windows build host (the deployment worker and
    /// the end-to-end run in the interactive session do not forward environment variables), else null.
    /// </summary>
    private static string? ConfiguredDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(Variable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        string agreed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "WinWispr", "test-data", "translation", "models");
        return Directory.Exists(agreed) ? agreed : null;
    }

    private static string Verify()
    {
        string directory = Path.GetFullPath(ConfiguredDirectory()!);
        Assert.IsTrue(Directory.Exists(directory), $"{Variable} does not name a folder: {directory}");
        var problems = new List<string>();
        foreach (BundledModelFile pinned in ModelManifest.Translation.Files)
        {
            string path = Path.Combine(directory, pinned.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add("missing " + pinned.RelativePath);
                continue;
            }

            long length = new FileInfo(path).Length;
            if (length != pinned.ExpectedBytes)
            {
                problems.Add($"{pinned.RelativePath} has {length} bytes, expected {pinned.ExpectedBytes}");
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (hash != pinned.Sha256)
                problems.Add($"{pinned.RelativePath} has SHA-256 {hash}, expected {pinned.Sha256}");
        }

        if (problems.Count > 0)
            Assert.Fail($"{Variable} ({directory}) is not the pinned translation model: " + string.Join("; ", problems));
        return directory;
    }

    internal static string DirectionDirectory(TranslationDirection direction) =>
        Path.Combine(Require(), OpusMtEngine.DirectionFolder(direction));
}
