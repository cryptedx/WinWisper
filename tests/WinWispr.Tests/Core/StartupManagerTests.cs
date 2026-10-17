using System.Reflection;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class StartupManagerTests
{
    [TestMethod]
    public void Disabled_startup_approval_states_are_not_treated_as_enabled()
    {
        Assert.IsFalse(StartupManager.IsStartupApprovalDisabled(null));
        Assert.IsFalse(StartupManager.IsStartupApprovalDisabled([0x02, 0x00, 0x00, 0x00]));
        Assert.IsTrue(StartupManager.IsStartupApprovalDisabled([0x03, 0x00, 0x00, 0x00]));
        Assert.IsTrue(StartupManager.IsStartupApprovalDisabled([0x09, 0x00, 0x00, 0x00]));
    }

    [TestMethod]
    public void Existing_startup_entry_is_migrated_to_the_current_executable()
    {
        MethodInfo? method = typeof(StartupManager).GetMethod(
            "MigrateRunValue",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        string? migrated = (string?)method!.Invoke(
            null,
            [
                "\"C:\\Users\\user\\Desktop\\WinWispr-portable-current\\WinWispr.exe\"",
                "C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe"
            ]);

        Assert.AreEqual(
            "\"C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\"",
            migrated);
    }
}
