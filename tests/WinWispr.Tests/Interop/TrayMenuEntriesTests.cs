using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class TrayMenuEntriesTests
{
    [TestMethod]
    public void The_menu_lists_translate_clipboard_between_start_stop_and_the_task_board()
    {
        CollectionAssert.AreEqual(
            new[] { "Settings", "Start/Stop", "Translate clipboard", "Task board", "Check for updates", "Exit" },
            TrayMenuEntries.Items.Select(item => item.Label).ToArray());
    }

    [TestMethod]
    public void Every_menu_command_is_recognized_and_other_values_are_not()
    {
        foreach ((TrayCommand command, _) in TrayMenuEntries.Items)
            Assert.IsTrue(TrayMenuEntries.IsMenuCommand((uint)command), command.ToString());
        Assert.IsFalse(TrayMenuEntries.IsMenuCommand(0));
        Assert.IsFalse(TrayMenuEntries.IsMenuCommand(1000));
    }

    [TestMethod]
    public void Menu_commands_have_distinct_values()
    {
        Assert.AreEqual(
            TrayMenuEntries.Items.Count, TrayMenuEntries.Items.Select(item => item.Command).Distinct().Count());
    }
}
