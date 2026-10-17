using System.Diagnostics;
using Microsoft.Win32;

namespace WinWispr.Core;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "WinWispr";
    private static readonly byte[] EnabledStartupApproval =
        [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    internal static bool IsEnabled()
    {
        try
        {
            // E2E must never migrate or change the user's Windows Run entry.
            if (AppDataPaths.IsTaskBoardE2e)
                return File.Exists(Path.Combine(AppDataPaths.Root, "startup.enabled"));
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value)
                return false;

            string? executablePath = Environment.ProcessPath;
            string? migratedValue = MigrateRunValue(value, executablePath);
            if (migratedValue is not null)
                key.SetValue(ValueName, migratedValue, RegistryValueKind.String);

            return (migratedValue is not null || IsCurrentExecutable(value, executablePath))
                && !IsStartupApprovalDisabled();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinWispr startup registration failed: {exception}");
            return false;
        }
    }

    private static string? MigrateRunValue(string? configuredValue, string? executablePath) =>
        !string.IsNullOrWhiteSpace(configuredValue)
            && !string.IsNullOrWhiteSpace(executablePath)
            && !IsCurrentExecutable(configuredValue, executablePath)
            ? $"\"{executablePath}\""
            : null;

    private static bool IsCurrentExecutable(string? configuredValue, string? executablePath) =>
        !string.IsNullOrWhiteSpace(configuredValue)
        && !string.IsNullOrWhiteSpace(executablePath)
        && string.Equals(
            configuredValue.Trim().Trim('"'),
            executablePath,
            StringComparison.OrdinalIgnoreCase);

    internal static bool TrySetEnabled(bool enabled)
    {
        try
        {
            if (AppDataPaths.IsTaskBoardE2e)
            {
                string marker = Path.Combine(AppDataPaths.Root, "startup.enabled");
                if (enabled)
                    File.WriteAllText(marker, "Enabled for this isolated test profile only.");
                else
                    File.Delete(marker);
                return true;
            }
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            if (enabled)
            {
                string executablePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The executable path is unavailable.");
                key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
                EnableStartupApprovalIfPresent();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinWispr startup setting update failed: {exception}");
            return false;
        }
    }

    internal static bool IsStartupApprovalDisabled(byte[]? approval) =>
        approval is { Length: > 0 } && approval[0] is 0x03 or 0x09;

    private static bool IsStartupApprovalDisabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath);
        return IsStartupApprovalDisabled(key?.GetValue(ValueName) as byte[]);
    }

    private static void EnableStartupApprovalIfPresent()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
        key?.SetValue(ValueName, EnabledStartupApproval, RegistryValueKind.Binary);
    }

}
