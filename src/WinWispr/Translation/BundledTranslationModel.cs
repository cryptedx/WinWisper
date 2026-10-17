using WinWispr.Core;
using WinWispr.Speech;

namespace WinWispr.Translation;

internal enum TranslationModelState
{
    Installed,

    /// <summary>
    /// The installation has no complete translation model: a file is missing or has the wrong size. This happens
    /// until the WinWispr Models installer has added it, or when files were deleted.
    /// </summary>
    Missing
}

/// <summary>
/// The translation model that ships in the WinWispr Models installer. It puts the ten files in the TranslationData
/// folder next to the executable, and they are read there in place: nothing is downloaded, copied or written. The
/// package build verified every size and SHA-256; here a file counts only while it has its pinned size.
/// </summary>
internal sealed class BundledTranslationModel
{
    /// <summary>The folder of the translation model that an earlier version downloaded into the app data.</summary>
    internal const string DownloadedCopyDirectory = "TranslationModels";

    private readonly string _directory;
    private readonly BundledModelSet _set;

    internal BundledTranslationModel(string directory, BundledModelSet? set = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _set = set ?? ModelManifest.Translation;
    }

    /// <summary>
    /// The model next to the executable. The E2E profile reads it from its own data root instead, so that a test
    /// can run the plain build output with or without the model and never touches an installation.
    /// </summary>
    internal static BundledTranslationModel CreateDefault() =>
        new(Path.Combine(
            AppDataPaths.IsTaskBoardE2e ? AppDataPaths.Root : AppContext.BaseDirectory,
            ModelManifest.Translation.PackageDirectory));

    /// <summary>The folder that holds both direction folders, or null when the model is not complete. Never throws.</summary>
    internal string? InstalledDirectory
    {
        get
        {
            try
            {
                foreach (BundledModelFile file in _set.Files)
                {
                    var info = new FileInfo(Path.Combine(_directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!info.Exists || info.Length != file.ExpectedBytes)
                        return null;
                }

                return _directory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    internal bool IsInstalled => InstalledDirectory is not null;

    internal TranslationModelState State => IsInstalled ? TranslationModelState.Installed : TranslationModelState.Missing;

    /// <summary>
    /// Deletes the copy of the translation model that an earlier version downloaded into the app data
    /// (<paramref name="appDataRoot"/>\TranslationModels). Nothing reads it any more. Returns false when it is
    /// still there afterwards; a later start tries again.
    /// </summary>
    internal static bool RemoveDownloadedCopy(string appDataRoot)
    {
        string path = Path.Combine(Path.GetFullPath(appDataRoot), DownloadedCopyDirectory);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
