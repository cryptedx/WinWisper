namespace WinWispr.Core;

internal static class AppDataPaths
{
    internal static bool IsTaskBoardE2e =>
        string.Equals(Environment.GetEnvironmentVariable("WINWISPR_E2E_MODE"), "taskboard", StringComparison.Ordinal);

    internal static string Root => IsTaskBoardE2e ? GetE2eRoot() : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinWispr");

    internal static string E2eInstanceId
    {
        get
        {
            if (!IsTaskBoardE2e || !Guid.TryParse(Environment.GetEnvironmentVariable("WINWISPR_E2E_INSTANCE_ID"), out Guid id))
                throw new InvalidOperationException("Taskboard E2E requires a valid instance id.");
            return id.ToString("N");
        }
    }

    internal static string E2eExitEventName => $@"Local\WinWispr-E2E-Exit-{E2eInstanceId}";

    /// <summary>
    /// The recorded clip that replaces the microphone, or null. Only the E2E profile can name one, and only a
    /// file directly inside the E2E data root, so a stray environment variable cannot redirect the
    /// microphone of a normal installation.
    /// </summary>
    internal static string? E2eReplayAudioPath
    {
        get
        {
            if (!IsTaskBoardE2e)
                return null;
            string? value = Environment.GetEnvironmentVariable("WINWISPR_E2E_AUDIO_PCM16");
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string path = Path.GetFullPath(value);
            if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("E2E replay audio must be a file directly inside the E2E data root.");
            FileInfo file = new(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("E2E replay audio must be an existing regular file.");
            return path;
        }
    }

    private static string GetE2eRoot()
    {
        string value = Environment.GetEnvironmentVariable("WINWISPR_E2E_DATA_ROOT")
            ?? throw new InvalidOperationException("Taskboard E2E requires a data root.");
        string root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relative = Path.GetRelativePath(temp, root);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Taskboard E2E data must be inside the Windows temporary directory.");
        for (DirectoryInfo? directory = new(root);
             directory is not null && !string.Equals(directory.FullName, temp, StringComparison.OrdinalIgnoreCase);
             directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Taskboard E2E data must not use a reparse point.");
        return root;
    }
}
