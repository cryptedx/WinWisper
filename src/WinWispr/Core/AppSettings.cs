using System.Diagnostics;
using System.Text.Json;
using WinWispr.Speech;

namespace WinWispr.Core;

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

internal enum RecordingMode
{
    Toggle,
    Hold
}

internal readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey);

internal readonly record struct WindowPlacement(int X, int Y, int Width, int Height)
{
    internal bool IsUsable => Width > 0 && Height > 0;

    internal static WindowPlacement Centered(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        int width,
        int height) => new WindowPlacement(areaX, areaY, width, height).ConstrainTo(
            areaX,
            areaY,
            areaWidth,
            areaHeight) with
        {
            X = areaX + Math.Max(0, (areaWidth - Math.Min(width, areaWidth)) / 2),
            Y = areaY + Math.Max(0, (areaHeight - Math.Min(height, areaHeight)) / 2)
        };

    internal WindowPlacement ConstrainTo(int areaX, int areaY, int areaWidth, int areaHeight)
    {
        int width = Math.Min(Math.Max(1, Width), areaWidth);
        int height = Math.Min(Math.Max(1, Height), areaHeight);
        int x = Math.Clamp(X, areaX, areaX + areaWidth - width);
        int y = Math.Clamp(Y, areaY, areaY + areaHeight - height);
        return new WindowPlacement(x, y, width, height);
    }

    internal WindowPlacement ConstrainTo(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        int minimumWidth,
        int minimumHeight)
    {
        int width = Math.Clamp(Width, Math.Min(Math.Max(1, minimumWidth), areaWidth), areaWidth);
        int height = Math.Clamp(Height, Math.Min(Math.Max(1, minimumHeight), areaHeight), areaHeight);
        int x = Math.Clamp(X, areaX, areaX + areaWidth - width);
        int y = Math.Clamp(Y, areaY, areaY + areaHeight - height);
        return new WindowPlacement(x, y, width, height);
    }
}

internal sealed record AppSettings(
    RecordingMode Mode,
    Hotkey Hotkey,
    string? MicrophoneId,
    WindowPlacement? SettingsWindowPlacement = null,
    bool VoiceCommandsEnabled = false,
    SpeechMode SpeechMode = SpeechMode.Automatic,
    Hotkey BoardHotkey = default,
    Hotkey TaskHotkey = default,
    RecordingMode TaskHotkeyMode = RecordingMode.Toggle,
    RecordingMode TaskButtonMode = RecordingMode.Toggle,
    WindowPlacement? TaskBoardWindowPlacement = null,
    int TaskDrawerWidth = 400,
    Hotkey EnglishDictationHotkey = default,
    RecordingMode EnglishDictationMode = RecordingMode.Hold,
    Hotkey TranslateHotkey = default,
    bool AutomaticUpdateChecks = true)
{
    internal static AppSettings Default => new(
        RecordingMode.Toggle,
        new Hotkey(HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x58),
        null,
        BoardHotkey: new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42),
        TaskHotkey: new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54),
        EnglishDictationHotkey: new Hotkey(HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x45),
        TranslateHotkey: new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x45));
}

internal enum SettingsSaveStatus
{
    Saved,
    Failed
}

internal readonly record struct SettingsSaveResult(SettingsSaveStatus Status, string? Message = null);

/// <summary>
/// Owns <c>settings.json</c>. The personal dictionary is not part of it; see <see cref="PersonalDictionaryStore"/>.
/// </summary>
internal sealed class AppSettingsStore(string path)
{
    internal const string SaveFailedMessage = "Could not save settings. Your existing files are preserved.";
    internal const string AccessDeniedMessage = "Could not save settings. Check disk access.";

    internal static AppSettingsStore Current { get; } = new(Path.Combine(AppDataPaths.Root, "settings.json"));
    private bool _canSave;
    private bool _recovered;
    internal string? LoadWarning { get; private set; }

    internal AppSettings Load()
    {
        _canSave = false;
        _recovered = false;
        LoadWarning = null;
        bool missing = false;
        try
        {
            AppSettings settings = Parse(File.ReadAllText(path));
            _canSave = true;
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            missing = error is FileNotFoundException or DirectoryNotFoundException;
            Trace.WriteLine($"WinWispr settings load failed: {error.GetType().Name}");
        }
        try
        {
            AppSettings settings = Parse(File.ReadAllText(path + ".bak"));
            _canSave = true;
            _recovered = true;
            LoadWarning = "Settings were recovered from a backup. Please check your settings.";
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _canSave = missing && (error is FileNotFoundException or DirectoryNotFoundException);
        }
        if (!_canSave)
            LoadWarning = "Settings could not be read. Your files are preserved and saving is disabled. Restore settings.json from a backup and restart WinWispr.";
        return AppSettings.Default;
    }

    internal static AppSettings Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a settings object.");
        T Read<T>(string name, T fallback)
        {
            try { return root.TryGetProperty(name, out var value) ? value.Deserialize<T>()! : fallback; }
            catch (JsonException) { return fallback; }
        }
        AppSettings defaults = AppSettings.Default;
        RecordingMode mode = Read("Mode", defaults.Mode);
        Hotkey hotkey = Read("Hotkey", defaults.Hotkey);
        Hotkey boardHotkey = Read("BoardHotkey", defaults.BoardHotkey);
        Hotkey taskHotkey = Read("TaskHotkey", defaults.TaskHotkey);
        Hotkey englishDictationHotkey = Read("EnglishDictationHotkey", defaults.EnglishDictationHotkey);
        RecordingMode englishDictationMode = Read("EnglishDictationMode", defaults.EnglishDictationMode);
        Hotkey translateHotkey = Read("TranslateHotkey", defaults.TranslateHotkey);
        string? microphone = Read<string?>("MicrophoneId", null);
        SpeechMode speech = Read("SpeechMode", SpeechMode.Automatic);
        const HotkeyModifiers allModifiers = HotkeyModifiers.Alt | HotkeyModifiers.Control
            | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
        return new AppSettings(
            Enum.IsDefined(mode) ? mode : defaults.Mode,
            (hotkey.Modifiers & ~allModifiers) == 0 && hotkey.VirtualKey is > 0 and <= byte.MaxValue
                ? hotkey : defaults.Hotkey,
            string.IsNullOrWhiteSpace(microphone) ? null : microphone,
            Read<WindowPlacement?>("SettingsWindowPlacement", null),
            Read("VoiceCommandsEnabled", false),
            Enum.IsDefined(speech) ? speech : SpeechMode.Automatic,
            IsStoredHotkeyValid(boardHotkey) ? boardHotkey : defaults.BoardHotkey,
            IsStoredHotkeyValid(taskHotkey) ? taskHotkey : defaults.TaskHotkey,
            Read("TaskHotkeyMode", RecordingMode.Toggle) is var taskHotkeyMode && Enum.IsDefined(taskHotkeyMode)
                ? taskHotkeyMode : RecordingMode.Toggle,
            Read("TaskButtonMode", RecordingMode.Toggle) is var taskButtonMode && Enum.IsDefined(taskButtonMode)
                ? taskButtonMode : RecordingMode.Toggle,
            Read<WindowPlacement?>("TaskBoardWindowPlacement", null),
            Math.Clamp(Read("TaskDrawerWidth", 400), 300, 700),
            IsStoredHotkeyValid(englishDictationHotkey) ? englishDictationHotkey : defaults.EnglishDictationHotkey,
            Enum.IsDefined(englishDictationMode) ? englishDictationMode : defaults.EnglishDictationMode,
            IsStoredHotkeyValid(translateHotkey) ? translateHotkey : defaults.TranslateHotkey,
            Read("AutomaticUpdateChecks", defaults.AutomaticUpdateChecks));
    }

    private static bool IsStoredHotkeyValid(Hotkey hotkey) =>
        hotkey.Modifiers != HotkeyModifiers.None
        && (hotkey.Modifiers & ~(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows)) == 0
        && hotkey.VirtualKey is > 0 and <= byte.MaxValue
        && hotkey.VirtualKey is not 0x1B and not 0x59;

    internal SettingsSaveResult Save(AppSettings settings)
    {
        if (!_canSave)
            return new(SettingsSaveStatus.Failed, SaveFailedMessage);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, _recovered ? path + ".corrupt-" + Guid.NewGuid().ToString("N") : path + ".bak");
            else
                File.Move(temporary, path);
            _recovered = false;
            return new(SettingsSaveStatus.Saved);
        }
        catch (IOException error)
        {
            Trace.WriteLine($"WinWispr settings save failed: {error.GetType().Name}");
            return new(SettingsSaveStatus.Failed, SaveFailedMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return new(SettingsSaveStatus.Failed, AccessDeniedMessage);
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
