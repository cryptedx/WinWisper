using System.Security.Cryptography;
using System.Text.Json;

namespace WinWispr.Speech;

internal sealed record TtsVoiceFile(string Path, long Bytes, string Sha256);
internal sealed record TtsVoice(string Id, string Language, string License, string Model,
    string Tokens, string DataDirectory, TtsVoiceFile[] Files)
{
    internal static TtsVoice Read(string directory)
    {
        directory = System.IO.Path.GetFullPath(directory);
        string manifest = Resolve(directory, "voice.json");
        if (new FileInfo(manifest).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Voice manifest exceeds the size limit.");
        TtsVoice voice = JsonSerializer.Deserialize<TtsVoice>(File.ReadAllText(manifest))
            ?? throw new InvalidDataException("Voice manifest is empty.");
        if (string.IsNullOrWhiteSpace(voice.Id) || voice.Language is not ("de-DE" or "en-US" or "en-GB")
            || string.IsNullOrWhiteSpace(voice.License) || voice.Files is not { Length: > 0 and <= 4096 })
            throw new InvalidDataException("Voice identity, language, license, and files are required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TtsVoiceFile file in voice.Files)
        {
            string path = Resolve(directory, file.Path);
            if (!names.Add(file.Path) || file.Bytes <= 0 || file.Bytes > 512L * 1024 * 1024
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)
                || new FileInfo(path).Length != file.Bytes)
                throw new InvalidDataException("Invalid voice file metadata.");
            using FileStream stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Voice file hash mismatch.");
        }
        if (!names.Contains(voice.Model) || !names.Contains(voice.Tokens)
            || !names.Contains(voice.License))
            throw new InvalidDataException("Model, tokens, and license must have verified hashes.");
        string data = Resolve(directory, voice.DataDirectory);
        if (!Directory.Exists(data) || !names.Any(n => n.StartsWith(voice.DataDirectory + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("Verified phonemizer data is required.");
        foreach (string path in EnumerateFiles(directory))
        {
            string relative = System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/');
            Resolve(directory, relative);
            if (relative != "voice.json" && !names.Contains(relative))
                throw new InvalidDataException("Unlisted voice files are not allowed.");
        }
        return voice;
    }

    private static IEnumerable<string> EnumerateFiles(string directory, int depth = 0)
    {
        if (depth > 16) throw new InvalidDataException("Voice directory nesting exceeds the limit.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked voice entries are not allowed.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string file in EnumerateFiles(entry, depth + 1)) yield return file;
            }
            else yield return entry;
        }
    }

    internal static string Resolve(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Voice paths must be relative, canonical paths.");
        string current = System.IO.Path.GetFullPath(directory);
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked voice directories are not allowed.");
        foreach (string part in relative.Split('/'))
        {
            current = System.IO.Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked voice files are not allowed.");
        }
        return current;
    }
}
