using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace WinWispr.Core.Updates;

internal enum InstallScope
{
    /// <summary>Setup installed WinWispr for this user (HKCU uninstall entry).</summary>
    CurrentUser,

    /// <summary>Setup installed WinWispr for all users (HKLM uninstall entry); updating asks for administrator rights.</summary>
    AllUsers,

    /// <summary>No Setup installed this folder: an extracted Portable ZIP or a development build.</summary>
    Portable
}

internal sealed record PendingUpdate(string Version);

/// <summary>
/// Hands the verified Setup to a helper that waits for WinWispr to exit, runs Setup silently into the same folder
/// and scope, and starts WinWispr again, the new version or, when Setup did not run, the old one.
/// </summary>
internal static class UpdateInstallation
{
    internal const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A2F3C9F1-51C4-4D9E-9C4A-7D7DDAA1D2A4}_is1";
    internal const string PolicyKey = @"Software\Policies\WinWispr";
    internal const string PolicyValue = "DisableUpdateCheck";
    private const string PendingFileName = "pending-update.json";

    internal static InstallScope DetectScope(string appDirectory, Func<bool, string?> readInstallLocation)
    {
        string app = Normalize(appDirectory);
        if (readInstallLocation(false) is { } user && string.Equals(Normalize(user), app, StringComparison.OrdinalIgnoreCase))
            return InstallScope.CurrentUser;
        if (readInstallLocation(true) is { } machine && string.Equals(Normalize(machine), app, StringComparison.OrdinalIgnoreCase))
            return InstallScope.AllUsers;
        return InstallScope.Portable;
    }

    internal static string? ReadInstallLocation(bool machine)
    {
        try
        {
            using RegistryKey? key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>An administrator turns update checks off with HKLM\Software\Policies\WinWispr DisableUpdateCheck = 1.</summary>
    internal static bool IsDisabledByPolicy()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(PolicyValue) is int value && value != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    internal static string BuildSetupArguments(InstallScope scope, string appDirectory, string logPath)
    {
        if (scope == InstallScope.Portable)
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "A Portable folder is not updated by Setup.");
        string scopeSwitch = scope == InstallScope.AllUsers ? "/ALLUSERS" : "/CURRENTUSER";
        return $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART {scopeSwitch} /DIR=\"{Normalize(appDirectory)}\" /LOG=\"{logPath}\"";
    }

    /// <summary>
    /// The PowerShell command of the helper. It waits for <paramref name="processId"/> to end, runs Setup (elevated
    /// for an all-users installation) and waits for it, then starts the app unelevated.
    /// </summary>
    internal static string BuildHelperCommand(
        int processId, string setupPath, string setupArguments, string appExecutable, bool elevate) =>
        string.Join("; ",
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Wait-Process -Id {processId} -Timeout 120",
            $"Start-Process -FilePath {Quote(setupPath)} -ArgumentList {Quote(setupArguments)} -Wait{(elevate ? " -Verb RunAs" : string.Empty)}",
            $"Start-Process -FilePath {Quote(appExecutable)}");

    internal static ProcessStartInfo CreateHelperStartInfo(string command)
    {
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", command })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static void WritePending(string updatesRoot, UpdateRelease release)
    {
        Directory.CreateDirectory(updatesRoot);
        File.WriteAllText(
            Path.Combine(updatesRoot, PendingFileName),
            JsonSerializer.Serialize(new PendingUpdate(release.DisplayVersion)));
    }

    /// <summary>Reads and removes the note an update left before it restarted WinWispr.</summary>
    internal static Version? TakePending(string updatesRoot)
    {
        string path = Path.Combine(updatesRoot, PendingFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            File.Delete(path);
            return AppVersion.Parse(JsonSerializer.Deserialize<PendingUpdate>(json)?.Version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
