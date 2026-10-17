using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core.Updates;

namespace WinWispr.Tests.Core.Updates;

[TestClass]
public sealed class UpdateInstallationTests
{
    private const string App = @"C:\Users\Test\AppData\Local\Programs\WinWispr";

    [TestMethod]
    public void The_uninstall_entry_that_names_this_folder_decides_the_scope()
    {
        Assert.AreEqual(InstallScope.CurrentUser,
            UpdateInstallation.DetectScope(App + @"\", machine => machine ? null : App + @"\"));
        Assert.AreEqual(InstallScope.AllUsers,
            UpdateInstallation.DetectScope(@"C:\Program Files\WinWispr\", machine => machine ? @"c:\program files\winwispr" : App));
        Assert.AreEqual(InstallScope.Portable,
            UpdateInstallation.DetectScope(@"D:\Tools\WinWispr", machine => machine ? @"C:\Program Files\WinWispr" : App));
        Assert.AreEqual(InstallScope.Portable, UpdateInstallation.DetectScope(App, _ => null));
    }

    [TestMethod]
    public void Setup_runs_silently_into_the_same_folder_and_scope()
    {
        Assert.AreEqual(
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=\"{App}\" /LOG=\"C:\\Temp\\setup.log\"",
            UpdateInstallation.BuildSetupArguments(InstallScope.CurrentUser, App + @"\", @"C:\Temp\setup.log"));
        StringAssert.Contains(
            UpdateInstallation.BuildSetupArguments(InstallScope.AllUsers, @"C:\Program Files\WinWispr", "log"),
            "/ALLUSERS /DIR=\"C:\\Program Files\\WinWispr\"");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => UpdateInstallation.BuildSetupArguments(InstallScope.Portable, App, "log"));
    }

    [TestMethod]
    public void The_helper_waits_for_the_app_runs_setup_and_starts_the_app_again()
    {
        string command = UpdateInstallation.BuildHelperCommand(
            4242, @"C:\Users\O'Brien\Setup.exe", "/VERYSILENT /DIR=\"C:\\x\"", @"C:\Apps\WinWispr.exe", elevate: false);

        Assert.AreEqual(
            "$ErrorActionPreference = 'SilentlyContinue'; Wait-Process -Id 4242 -Timeout 120; "
            + "Start-Process -FilePath 'C:\\Users\\O''Brien\\Setup.exe' -ArgumentList '/VERYSILENT /DIR=\"C:\\x\"' -Wait; "
            + "Start-Process -FilePath 'C:\\Apps\\WinWispr.exe'",
            command);
        StringAssert.Contains(
            UpdateInstallation.BuildHelperCommand(1, "s", "a", "e", elevate: true),
            "-ArgumentList 'a' -Wait -Verb RunAs; Start-Process -FilePath 'e'");
    }

    [TestMethod]
    public void The_pending_note_is_read_once()
    {
        using var directory = new TemporaryDirectory();
        var release = new UpdateRelease(new Version(1, 11, 0), new Uri("https://github.com/a"), "s.exe", 1, null,
            new Uri("https://github.com/b"), new Uri("https://github.com/c"));

        UpdateInstallation.WritePending(directory.Path, release);

        Assert.AreEqual(new Version(1, 11, 0), UpdateInstallation.TakePending(directory.Path));
        Assert.IsNull(UpdateInstallation.TakePending(directory.Path));
    }
}
