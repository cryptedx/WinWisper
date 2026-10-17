using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class TtsPackagingTests
{
    [TestMethod]
    public Task Offline_packaging_checks_both_languages_and_rejects_corruption() => RunAsync(false);

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public Task Real_release_voice_payload_matches_pinned_manifests() => RunAsync(true);

    private static async Task RunAsync(bool realVoices)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinWispr.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root.FullName, "tests", "TtsPackaging.Tests.ps1") }) info.ArgumentList.Add(arg);
        if (realVoices) info.ArgumentList.Add("-RealVoices");
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(realVoices ? 900 : 30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }
}
