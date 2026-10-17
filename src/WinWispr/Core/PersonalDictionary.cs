using System.Diagnostics;
using System.Text.Json;

namespace WinWispr.Core;

/// <summary>The parsed personal dictionary: phrase = replacement entries applied to every transcript.</summary>
internal sealed record PersonalDictionary(IReadOnlyDictionary<string, string> Entries)
{
    internal static PersonalDictionary Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    internal static bool TryParse(string? input, out PersonalDictionary dictionary, out string? error)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        dictionary = Empty;
        error = null;
        if (input is null || input.Length > 41_000)
        {
            error = "Use at most 100 entries, with up to 200 characters per phrase.";
            return false;
        }

        int lineNumber = 0;
        foreach (string line in input.Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int separator = line.IndexOf('=');
            string source = separator < 0 ? "" : line[..separator].Trim();
            string replacement = separator < 0 ? "" : line[(separator + 1)..].Trim();
            if (source.Length is 0 or > 200 || replacement.Length is 0 or > 200
                || !source.Any(char.IsLetterOrDigit)
                || source.Any(char.IsControl) || replacement.Any(char.IsControl)
                || entries.Count >= 100 || !entries.TryAdd(source, replacement))
            {
                error = $"Check line {lineNumber}: use a unique phrase = replacement (1–200 characters each; maximum 100 entries).";
                return false;
            }
        }
        dictionary = new PersonalDictionary(entries);
        return true;
    }
}

internal enum DictionarySaveStatus
{
    Saved,
    Invalid,
    Locked,
    Failed
}

internal readonly record struct DictionarySaveResult(DictionarySaveStatus Status, string? Message = null);

/// <summary>
/// Owns <c>settings.json.dictionary</c> (with <c>.bak</c>), the only store of the personal dictionary.
/// It never depends on <c>settings.json</c> being readable; the legacy field there is only migrated.
/// </summary>
internal sealed class PersonalDictionaryStore(string settingsPath)
{
    internal const string RecoveredFromBackupWarning =
        "The personal dictionary was recovered from its backup. Please check your entries.";
    internal const string UnreadableWarning =
        "The personal dictionary could not be read or migrated. Your files are preserved and saving is disabled.";
    internal const string ChangedOnDiskMessage =
        "The personal dictionary changed on disk. Restart WinWispr before saving.";
    internal const string SaveFailedMessage = "Could not save the dictionary. Check disk access and try again.";

    internal static PersonalDictionaryStore Current { get; } =
        new(Path.Combine(AppDataPaths.Root, "settings.json"));

    private string DictionaryPath => settingsPath + ".dictionary";
    private string? _onDisk;

    internal string Text { get; private set; } = "";
    internal PersonalDictionary Dictionary { get; private set; } = PersonalDictionary.Empty;
    internal string? LoadWarning { get; private set; }
    internal bool CanEdit { get; private set; }

    internal void Load()
    {
        CanEdit = false;
        LoadWarning = null;
        _onDisk = null;
        string text = "";
        try
        {
            string? file = ReadOptionalText(DictionaryPath);
            if (file is not null)
            {
                // The file is the source of truth, even when empty. A differing legacy copy in
                // settings.json is kept once, because the next settings save drops that field.
                text = file;
                string? legacy = ReadLegacyText(settingsPath);
                if (!string.IsNullOrEmpty(legacy) && legacy != file && !File.Exists(DictionaryPath + ".legacy"))
                    WriteNewFile(DictionaryPath + ".legacy", legacy);
            }
            else if (ReadOptionalText(DictionaryPath + ".bak") is string backup)
            {
                text = backup;
                WriteNewFile(DictionaryPath, backup);
                LoadWarning = RecoveredFromBackupWarning;
            }
            else
            {
                string? legacy = ReadLegacyText(settingsPath);
                if (string.IsNullOrEmpty(legacy))
                    legacy = ReadLegacyText(settingsPath + ".bak");
                if (!string.IsNullOrEmpty(legacy))
                {
                    text = legacy;
                    WriteNewFile(DictionaryPath, legacy);
                }
            }
            _onDisk = ReadOptionalText(DictionaryPath);
            CanEdit = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LoadWarning = UnreadableWarning;
            Trace.WriteLine($"WinWispr personal dictionary load failed: {error.GetType().Name}");
        }
        Text = text;
        Dictionary = PersonalDictionary.TryParse(text, out PersonalDictionary parsed, out _) ? parsed : PersonalDictionary.Empty;
    }

    internal DictionarySaveResult Replace(string text)
    {
        if (!PersonalDictionary.TryParse(text, out PersonalDictionary parsed, out string? error))
            return new(DictionarySaveStatus.Invalid, error);
        if (!CanEdit)
            return new(DictionarySaveStatus.Locked, LoadWarning ?? UnreadableWarning);
        if (text == Text)
            return new(DictionarySaveStatus.Saved);
        try
        {
            if (ReadOptionalText(DictionaryPath) != _onDisk)
                return new(DictionarySaveStatus.Locked, ChangedOnDiskMessage);
            WriteWithBackup(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"WinWispr personal dictionary save failed: {exception.GetType().Name}");
            return new(DictionarySaveStatus.Failed, SaveFailedMessage);
        }
        _onDisk = text;
        Text = text;
        Dictionary = parsed;
        return new(DictionarySaveStatus.Saved);
    }

    private void WriteWithBackup(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DictionaryPath)!);
        string temporary = WriteTemporary(DictionaryPath, text);
        try
        {
            if (File.Exists(DictionaryPath))
                File.Replace(temporary, DictionaryPath, DictionaryPath + ".bak");
            else
                File.Move(temporary, DictionaryPath);
        }
        finally { File.Delete(temporary); }
    }

    private static void WriteNewFile(string destination, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = WriteTemporary(destination, text);
        try { File.Move(temporary, destination); }
        finally { File.Delete(temporary); }
    }

    private static string WriteTemporary(string destination, string text)
    {
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        return temporary;
    }

    private static string? ReadOptionalText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Reads the pre-sidecar <c>PersonalDictionary</c> field; an unreadable settings file has none.</summary>
    private static string? ReadLegacyText(string file)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("PersonalDictionary", out JsonElement entry)
                && entry.ValueKind == JsonValueKind.String
                ? entry.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }
}
