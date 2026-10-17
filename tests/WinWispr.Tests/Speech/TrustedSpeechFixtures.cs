using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

/// <summary>
/// The speech model and the German speech clip for acceptance tests, both taken from the pinned Parakeet model
/// archive. The archive is trusted because its size and SHA-256 match <see cref="ModelManifest"/>; the clip
/// (<c>test_wavs/de.wav</c> inside it) inherits that trust, and its own SHA-256 is pinned below so a changed archive
/// cannot swap it unnoticed. The model is installed with the app's own <see cref="ModelInstaller"/>, which checks the
/// archive and the extracted file sizes again.
/// </summary>
internal static class TrustedSpeechFixtures
{
    /// <summary>SHA-256 of <c>test_wavs/de.wav</c> in the archive pinned by <see cref="ModelManifest.Parakeet"/>.</summary>
    internal const string GermanClipSha256 = "36d3c4845b9808a1656a2a2e92d884590e2db94389e6fe559643291ae0cd3710";

    private static readonly ModelSpec Model = ModelManifest.Parakeet;
    private static readonly string ArchiveName = Model.Uri.Segments[^1];
    private static readonly string ArchiveRoot = Model.Files[0].ArchivePath.Split('/')[0];
    private static readonly string GermanClipArchivePath = ArchiveRoot + "/test_wavs/de.wav";
    private static readonly Lazy<(Fixtures? Value, string Missing)> Prepared = new(Prepare);

    /// <param name="ModelsRoot">An app <c>Models</c> folder (active.json and installs) holding only the pinned model.</param>
    /// <param name="ModelDirectory">The installed model files.</param>
    /// <param name="GermanClip">The verified German speech clip.</param>
    internal sealed record Fixtures(string ModelsRoot, string ModelDirectory, string GermanClip);

    /// <summary>The verified fixtures, or Inconclusive when the pinned archive is not available on this machine.</summary>
    internal static Fixtures Require()
    {
        (Fixtures? value, string missing) = Prepared.Value;
        if (value is null)
            Assert.Inconclusive(missing);
        return value!;
    }

    private static (Fixtures?, string) Prepare()
    {
        string? archive = FindArchive();
        if (archive is null)
            return (null, $"Speech acceptance needs the pinned model archive {ArchiveName}: set WINWISPR_MODEL_ARCHIVE "
                + "or provide artifacts/model in the checkout (a deployment or ./eng/deploy-windows-vm.sh --check copies it).");

        string cache = Path.Combine(Path.GetTempPath(), "WinWispr-speech-fixtures", Model.ArchiveSha256[..16]);
        // Another test process can prepare the same cache; the mutex keeps one extraction at a time.
        using var mutex = new Mutex(false, @"Local\WinWispr-speech-fixtures");
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            return (Load(cache) ?? Create(archive, cache), string.Empty);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string? FindArchive()
    {
        string? configured = Environment.GetEnvironmentVariable("WINWISPR_MODEL_ARCHIVE");
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "artifacts", "model", ArchiveName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // Reuses a cache that an earlier run built from a verified archive, after checking its files again.
    private static Fixtures? Load(string cache)
    {
        string modelsRoot = Path.Combine(cache, "app", "Models");
        string clip = Path.Combine(cache, "de.wav");
        string? modelDirectory = CreateInstaller(cache).GetActiveModelDirectory();
        if (modelDirectory is null || !File.Exists(clip) || Sha256(clip) != GermanClipSha256)
            return null;
        foreach (ModelFileSpec file in Model.Files)
        {
            var info = new FileInfo(Path.Combine(modelDirectory, file.InstalledName));
            if (!info.Exists || info.Length != file.ExpectedBytes)
                return null;
        }

        return new Fixtures(modelsRoot, modelDirectory, clip);
    }

    private static Fixtures Create(string archive, string cache)
    {
        if (Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);

        ModelInstaller installer = CreateInstaller(cache, archive);
        ModelInstallStatus status = installer.InstallBundledAsync(progress: null, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (status != ModelInstallStatus.Installed)
            throw new AssertFailedException(
                $"The model archive at {archive} failed verification or extraction ({status}); it is not the pinned archive.");

        ExtractGermanClip(archive, cache);
        string extracted = Path.Combine(cache, GermanClipArchivePath.Replace('/', Path.DirectorySeparatorChar));
        string clip = Path.Combine(cache, "de.wav");
        File.Move(extracted, clip);
        Directory.Delete(Path.Combine(cache, ArchiveRoot), recursive: true);
        string clipHash = Sha256(clip);
        if (clipHash != GermanClipSha256)
            throw new AssertFailedException($"{GermanClipArchivePath} in {archive} has SHA-256 {clipHash}, not the pinned {GermanClipSha256}.");

        return Load(cache) ?? throw new AssertFailedException($"The speech fixtures in {cache} did not verify after extraction.");
    }

    private static ModelInstaller CreateInstaller(string cache, string? archive = null) =>
        new(Path.Combine(cache, "app"), new HttpClient(), bundledArchivePath: archive ?? Path.Combine(cache, "none"));

    private static void ExtractGermanClip(string archive, string cache)
    {
        var startInfo = new ProcessStartInfo("tar.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-xjf", archive, "-C", cache, GermanClipArchivePath })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new AssertFailedException("tar.exe could not be started.");
        Task<string> error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new AssertFailedException($"tar.exe could not extract {GermanClipArchivePath}: {error.Result}");
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
