using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using WinWispr.Core;

namespace WinWispr.Tests.Views;

[TestClass]
[DoNotParallelize]
public sealed class TaskBoardEndToEndTests
{
    private const string E2eModeVariable = "WINWISPR_E2E_MODE";
    private const string E2eRootVariable = "WINWISPR_E2E_DATA_ROOT";
    private const string E2eInstanceVariable = "WINWISPR_E2E_INSTANCE_ID";
    private const string BoardWindowName = "WinWispr Tasks";
    private const string CreatedTitle = "E2E task";
    private const string UpdatedTitle = "E2E task updated";
    private const string CreatedTag = "E2E tag";
    private const string RenamedTag = "E2E renamed tag";
    private const string TemporaryTag = "E2E temporary tag";
    private const string SeedTag = "Seed tag";
    private const string SuggestedTag = "Suggested tag";
    private const string CaptureOpenTitle = "Captured UI task";
    private const string CaptureUndoTitle = "Undone UI task";
    private const string UpdatedDescription = "Blackbox description";
    private const string RecoveredDescription = "Recovered description";

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_task_board_flow_survives_process_restart()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinWispr-E2E", Guid.NewGuid().ToString("N"));
        string instanceId = Guid.NewGuid().ToString("N");
        string eventName = $@"Local\WinWispr-E2E-Exit-{instanceId}";
        DateOnly expectedDueDate = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        Directory.CreateDirectory(root);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        try
        {
            SeedProfile(root);
            SeedDraft(root, "Discardable draft", expectedDueDate);
            RunApplication(root, instanceId, exitEvent, ExerciseDueViewAndDraftDiscard, "--e2e-taskboard-due");
            Assert.IsNull(ReadBoard(root).Draft);

            SeedDraft(root, "Openable draft", expectedDueDate);
            RunApplication(root, instanceId, exitEvent, driver => ExerciseTaskCaptureOpen(driver, root, expectedDueDate));

            SeedDraft(root, "Undoable draft", expectedDueDate);
            RunApplication(root, instanceId, exitEvent, driver => ExerciseTaskCaptureUndo(driver, root));

            RunApplication(root, instanceId, exitEvent, driver => ExerciseTaskBoard(driver, root, expectedDueDate));
            AssertPersisted(root, expectedDueDate);
            RunApplication(root, instanceId, exitEvent, driver => VerifyRestart(driver, root));
            AssertPersisted(root, expectedDueDate);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_task_board_recovery_actions_are_available_through_the_ui()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinWispr-E2E", Guid.NewGuid().ToString("N"));
        string instanceId = Guid.NewGuid().ToString("N");
        string eventName = $@"Local\WinWispr-E2E-Exit-{instanceId}";
        Directory.CreateDirectory(root);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        try
        {
            SeedProfile(root);
            SeedStaleRecovery(root);
            RunApplication(root, instanceId, exitEvent, ExerciseDiscardStaleRecovery);
            Assert.IsFalse(File.Exists(Path.Combine(root, "tasks.json.recovery")), "stale recovery should be discarded through the UI");

            SeedRecoverableEdit(root);
            RunApplication(root, instanceId, exitEvent, ExerciseAcknowledgeRecoveredData);
            Assert.AreEqual(RecoveredDescription, ReadBoard(root).Tasks.Single(task => task.Title == "Seed task").Description);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_task_board_hotkey_toggles_after_recovered_edit()
    {
        WithProfile((root, instance, exit) =>
        {
            SeedRecoverableEdit(root);
            var settingsStore = new AppSettingsStore(Path.Combine(root, "settings.json"));
            AppSettings settings = settingsStore.Load();
            settingsStore.Save(settings with
            {
                BoardHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7A)
            });
            RunApplication(root, instance, exit, ui =>
            {
                var hotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7A);
                ui.WaitForName("Task Seed task, To Do", "recovered task board visible");
                long initialRevision = ReadBoard(root).Revision;
                ui.PressHotkey(hotkey, "hide board after recovered edit");
                ui.WaitForBoardHidden("board hidden after first hotkey");
                ui.Until(() => ReadBoard(root).Revision > initialRevision, "first recovery save completed");
                ui.PressHotkey(hotkey, "show board after recovered edit");
                ui.WaitForBoardVisible("board visible after second hotkey");
                ui.PressHotkey(hotkey, "hide board a second time after recovered edit");
                ui.WaitForBoardHidden("board hidden after third hotkey");
                ui.PressHotkey(hotkey, "restore board before E2E shutdown");
                ui.WaitForBoardVisible("board visible before E2E shutdown");
                ui.InvokeId("board-menu", "open recovery menu before E2E shutdown");
                ui.InvokeId("board-menu-acknowledge-recovery", "acknowledge recovered data before E2E shutdown");
            });
        });
    }

    private static void SeedProfile(string root)
    {
        string tasksPath = Path.Combine(root, "tasks.json");
        var store = new TaskBoardStore(tasksPath);
        TaskBoardDocument document = store.Load();
        BoardTag seedTag = new() { Name = "Seed tag", Color = "blue" };
        BoardTag suggestedTag = new() { Name = SuggestedTag, Color = "green" };
        document.Tags.Add(seedTag);
        document.Tags.Add(suggestedTag);
        TaskBoardOperations.CreateTask(
            document,
            "Seed task",
            "Seed description",
            string.Empty,
            DateOnly.FromDateTime(DateTime.Now),
            [seedTag.Id],
            DateTimeOffset.UtcNow);
        document.LastSummaryLocalDate = DateOnly.FromDateTime(DateTime.Now);
        store.Save(document);

        var settingsStore = new AppSettingsStore(Path.Combine(root, "settings.json"));
        AppSettings settings = settingsStore.Load();
        settingsStore.Save(settings with
        {
            BoardHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42),
            TaskHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54),
            TaskBoardWindowPlacement = null
        });
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_task_board_add_drawer_suggestions_and_card_layout_work()
    {
        WithProfile((root, instance, exit) =>
        {
            var store = new TaskBoardStore(Path.Combine(root, "tasks.json"));
            TaskBoardDocument document = store.Load();
            TaskBoardOperations.CreateTask(
                document,
                "Plan Seed tag follow-up",
                "This task intentionally has no tag.",
                string.Empty,
                null,
                [],
                DateTimeOffset.UtcNow.AddMinutes(1));
            store.Save(document);

            RunApplication(root, instance, exit, ui =>
            {
                ui.ResizeWindow(1200, 900, "use the wide board for layout checks");
                ui.WaitForGone("Add task to To Do", "lower add task button removed");
                ui.InvokeId("add-task", "open quick task entry from the top button");
                ui.WaitForId("new-task-title", "quick task entry focused and visible");
                ui.PressEscape("cancel layout test quick task");

                AutomationElement first = ui.WaitForName("Task Seed task, To Do", "find first card for width check");
                AutomationElement second = ui.WaitForName("Task Plan Seed tag follow-up, To Do", "find second card for width check");
                Assert.IsTrue(Math.Abs(first.Current.BoundingRectangle.Width - second.Current.BoundingRectangle.Width) < 3,
                    "Cards in one column must have the same width.");

                ui.Click(first, "open first task drawer with a real pointer click");
                ui.Click(second, "switch directly to the second task drawer with a real pointer click");
                Assert.AreEqual("Plan Seed tag follow-up", ui.GetValue("task-title", "read switched task title"));
                Guid seedTagId = ReadTagId(root, SeedTag);
                ui.InvokeId($"suggested-task-tag-{seedTagId:N}", "apply text-based tag suggestion");
                ui.WaitForId($"remove-task-tag-{seedTagId:N}", "suggested tag assigned in drawer");
                ui.InvokeId("close-task-details", "close task drawer after layout checks");
            });
        });
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_sort_filter_and_reorder_results_are_exact()
    {
        WithProfile((root, instance, exit) =>
        {
            var store = new TaskBoardStore(Path.Combine(root, "tasks.json"));
            TaskBoardDocument document = store.Load();
            Guid seed = document.Tags.Single(tag => tag.Name == SeedTag).Id;
            Guid other = document.Tags.Single(tag => tag.Name == SuggestedTag).Id;
            document.Tasks =
            [
                new() { Title = "Atlas", ManualOrder = 30, CreatedUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DueDate = new(2026, 1, 3), TagIds = [seed] },
                new() { Title = "Birch", Description = "Unique needle", ManualOrder = 10, CreatedUtc = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), DueDate = new(2026, 1, 1), TagIds = [other] },
                new() { Title = "Cedar", ManualOrder = 20, CreatedUtc = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TagIds = [seed] }
            ];
            store.Save(document);
            RunApplication(root, instance, exit, ui =>
            {
                ui.ResizeWindow(1200, 900, "use the three-column board");
                foreach ((string sort, string[] expected) in new (string, string[])[]
                {
                    ("Sort: Manual", ["Birch", "Cedar", "Atlas"]),
                    ("Title: A–Z", ["Atlas", "Birch", "Cedar"]),
                    ("Title: Z–A", ["Cedar", "Birch", "Atlas"]),
                    ("Due date: Earliest", ["Birch", "Atlas", "Cedar"]),
                    ("Due date: Latest", ["Atlas", "Birch", "Cedar"]),
                    ("Created: Newest", ["Birch", "Cedar", "Atlas"]),
                    ("Created: Oldest", ["Atlas", "Cedar", "Birch"])
                })
                {
                    ui.Select("task-sort", sort, "select " + sort);
                    ui.ExpectCards("todo", expected);
                }
                ui.Select("task-sort", "Sort: Manual", "restore manual order");
                ui.InvokeName("Task Atlas, To Do", "open task with empty description");
                AutomationElement addDescription = ui.WaitForId("edit-task-description", "find enlarged add description area");
                Assert.IsTrue(addDescription.Current.BoundingRectangle.Height >= 96, "Empty description action must provide a large click target.");
                ui.Click(addDescription, "click empty description area");
                ui.WaitForId("task-description", "open empty description editor");
                ui.PressEscape("close empty description editor");
                ui.InvokeId("close-task-details", "close empty description task");
                ui.InvokeId($"tag-filter-{seed:N}", "filter by Seed tag");
                ui.ExpectCards("todo", ["Cedar", "Atlas"]);
                ui.SetValue("task-search", "unique needle", "combine description search with tag filter");
                ui.ExpectCards("todo", []);
                ui.InvokeId("tag-filter-all", "clear tag filter");
                ui.ExpectCards("todo", ["Birch"]);
                ui.SetValue("task-search", "  aTlAs  ", "case-insensitive trimmed title search");
                ui.ExpectCards("todo", ["Atlas"]);
                ui.SetValue("task-search", SeedTag, "search tag text");
                ui.ExpectCards("todo", ["Cedar", "Atlas"]);
                ui.SetValue("task-search", "no matching task", "empty search result");
                ui.ExpectCards("todo", []);
                ui.SetValue("task-search", "", "clear search");
                ui.ExpectCards("todo", ["Birch", "Cedar", "Atlas"]);
                OpenTaskMenu(ui, "Task Birch, To Do");
                ui.InvokeId("task-menu-move-down", "move Birch down");
                ui.ExpectCards("todo", ["Cedar", "Birch", "Atlas"]);
                OpenTaskMenu(ui, "Task Birch, To Do");
                ui.InvokeId("task-menu-move-up", "move Birch up");
                ui.ExpectCards("todo", ["Birch", "Cedar", "Atlas"]);
                ui.Drag(ui.WaitForName("Task Birch, To Do", "cancelled drag source"), ui.WaitForName("Task Atlas, To Do", "cancelled drag target"),
                    "cancel reordering with Escape", cancel: true);
                ui.ExpectCards("todo", ["Birch", "Cedar", "Atlas"]);
                ui.Drag(ui.WaitForName("Task Birch, To Do", "drag Birch"), ui.WaitForName("Task Atlas, To Do", "drop before Atlas"), "reorder Birch before Atlas");
                ui.ExpectCards("todo", ["Cedar", "Birch", "Atlas"]);
                ui.Drag(ui.WaitForName("Task Birch, To Do", "drag Birch to Doing"), ui.WaitForId("task-list-doing", "Doing column"), "move Birch between columns");
                ui.ExpectCards("todo", ["Cedar", "Atlas"]);
                ui.ExpectCards("doing", ["Birch"]);
                ui.InvokeName("Task Atlas, To Do", "open drawer for resize");
                int before = ReadBoard(root).View.DrawerWidth;
                ui.DragBy("resize-task-drawer", -80, "resize task drawer");
                ui.Until(() => ReadBoard(root).View.DrawerWidth > before, "drawer width persisted");
                ui.InvokeId("close-task-details", "close resized drawer");
                ui.ResizeWindow(650, 850, "switch to narrow board");
                ui.InvokeId("status-tab-doing", "select narrow Doing tab");
                ui.ExpectCards("doing", ["Birch"]);
                ui.ExpectHiddenId("task-list-todo", "To Do column hidden in narrow mode");
                ui.InvokeId("status-tab-done", "select empty Done tab");
                ui.ExpectCards("done", []);
                ui.InvokeId("status-tab-todo", "return to To Do tab");
                ui.ExpectCards("todo", ["Cedar", "Atlas"]);
            });
            RunApplication(root, instance, exit, ui =>
            {
                ui.ResizeWindow(1200, 900, "verify wide board after restart");
                ui.ExpectCards("todo", ["Cedar", "Atlas"]);
                ui.ExpectCards("doing", ["Birch"]);
                Assert.IsTrue(ReadBoard(root).View.DrawerWidth > 400);
            });
        });
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_retains_invalid_edits_and_retries_failed_saves()
    {
        WithProfile((root, instance, exit) =>
        {
            RunApplication(root, instance, exit, ui =>
            {
                ui.InvokeName("Task Seed task, To Do", "open task for validation");
                ui.SetValue("task-title", "", "reject empty title");
                ui.WaitForName("Title is required", "empty title warning");
                Assert.AreEqual("Seed task", ReadBoard(root).Tasks.Single().Title);
                string tooLong = new('x', 401);
                ui.SetValue("task-title", tooLong, "reject long title");
                ui.WaitForName("Task title is too long", "long title warning");
                Assert.AreEqual(tooLong, ui.GetValue("task-title", "retain invalid title"));
                ui.SetValue("task-title", "Seed task", "correct title");
                ui.InvokeId("edit-task-description", "edit description for validation");
                ui.SetValue("task-description", new string('x', 100001), "reject long description");
                ui.WaitForName("Description is too long", "description validation warning");
                Assert.AreEqual(100001, ui.GetValue("task-description", "retain long description").Length);
                ui.SetValue("task-description", "Valid description", "correct description");
                ui.PressEscape("finish description editing");
                ui.InvokeId("close-task-details", "flush valid edits");
                ui.Until(() => ReadBoard(root).Tasks.Single().Description == "Valid description", "valid edits saved");

                // Deny replacement only inside this test's temporary profile.
                using (var locked = new FileStream(Path.Combine(root, "tasks.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    OpenTaskMenu(ui, "Task Seed task, To Do");
                    ui.InvokeId("task-menu-move-doing", "save metadata while storage is unavailable");
                    ui.WaitForName("Retry saving task board", "retry action visible");
                    ui.Until(() => File.Exists(Path.Combine(root, "tasks.json.recovery")), "recovery file created");
                    Assert.AreEqual(BoardTaskStatus.ToDo, ReadBoard(root).Tasks.Single().Status);
                }
                ui.InvokeName("Retry saving task board", "retry failed save");
                ui.Until(() => ReadBoard(root).Tasks.Single().Status == BoardTaskStatus.Doing, "retry persists metadata");
            });
            RunApplication(root, instance, exit, ui =>
            {
                ui.ShowStatusIfNeeded("Task Seed task, Doing", "Doing", "show recovered metadata");
                ui.WaitForName("Task Seed task, Doing", "retried task survives restart");
                ui.InvokeName("Task Seed task, Doing", "read retried task");
                Assert.AreEqual("Doing", ui.GetSelectionName("task-status", "read retried status"));
                ui.InvokeId("edit-task-description", "read valid description after restart");
                Assert.AreEqual("Valid description", ui.GetValue("task-description", "valid description survived"));
            });
        });
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_keeps_every_line_of_a_multi_line_description_when_editing()
    {
        const string MultiLine = "First line\rSecond line\r- third line";
        WithProfile((root, instance, exit) =>
        {
            var store = new TaskBoardStore(Path.Combine(root, "tasks.json"));
            TaskBoardDocument document = store.Load();
            document.Tasks.Single().Description = MultiLine;
            store.Save(document);
            SeedDraft(root, "Multi-line draft", DateOnly.FromDateTime(DateTime.Now));
            document = store.Load();
            document.Draft!.Description = MultiLine;
            store.Save(document);
            RunApplication(root, instance, exit, ui =>
            {
                ui.InvokeName("Task Seed task, To Do", "open task with a multi-line description");
                ui.InvokeId("edit-task-description", "open the description editor");
                Assert.AreEqual(MultiLine, ui.GetValue("task-description", "editor shows every line"));
                ui.InvokeId("close-task-details", "leave the editor");
                ui.InvokeId("close-task-details", "close the details");
                Assert.AreEqual(MultiLine, ReadBoard(root).Tasks.Single().Description, "Saved description after editing");

                UiDriver capture = OpenDraftCapture(ui);
                Assert.AreEqual(MultiLine, capture.GetValue("capture-task-description", "capture editor shows every line"));
            });
        });
    }

    private static TaskBoardDocument ReadBoard(string root) =>
        JsonSerializer.Deserialize<TaskBoardDocument>(File.ReadAllText(Path.Combine(root, "tasks.json")))!;

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_draft_calendar_and_export_survive_restart()
    {
        WithProfile((root, instance, exit) =>
        {
            SeedDraft(root, "Calendar draft", new DateOnly(2026, 12, 31));
            RunApplication(root, instance, exit, ui =>
            {
                UiDriver capture = OpenDraftCapture(ui);
                capture.SetValue("capture-task-title", "", "reject empty capture title");
                capture.InvokeId("capture-add-task", "try saving invalid draft");
                capture.WaitForName("Could not save the task. Your edits remain here; check disk access and try again.", "invalid capture remains open");
                Assert.AreEqual(1, ReadBoard(root).Tasks.Count);
                capture.SetValue("capture-task-title", "Draft edited through UI", "edit draft title");
                capture.SetValue("capture-task-description", "Persisted draft description", "edit draft description");
                capture.AdvanceCalendarDate("capture-task-due-date", "advance from December to January");
                capture.Until(() => ReadBoard(root).Draft?.SuggestedDueDate == new DateOnly(2027, 1, 1), "calendar commits January 1");
                capture.SetValue("capture-task-tags", "Persisted draft tag", "add draft tag");
                capture.PressEnter("commit draft tag");
                capture.InvokeId("capture-cancel", "hide edited draft");
                capture.WaitForWindowGone("New task", "draft capture hidden");
            });
            RunApplication(root, instance, exit, ui =>
            {
                UiDriver capture = OpenDraftCapture(ui);
                Assert.AreEqual("Draft edited through UI", capture.GetValue("capture-task-title", "draft title after restart"));
                Assert.AreEqual("Persisted draft description", capture.GetValue("capture-task-description", "draft description after restart"));
                capture.WaitForName("Remove capture tag Persisted draft tag", "draft tag after restart");
                Assert.AreEqual(new DateOnly(2027, 1, 1), ReadBoard(root).Draft!.SuggestedDueDate);
                capture.SetValue("capture-task-title", "Calendar task", "name captured task");
                capture.PressEnter("save captured task by keyboard");
                capture.WaitForWindowGone("New task", "capture saved by Enter");
                ui.WaitForName("Task Calendar task, To Do", "saved calendar task");
                BoardTask task = ReadBoard(root).Tasks.Single(task => task.Title == "Calendar task");
                Assert.AreEqual("Persisted draft description", task.Description);
                Assert.AreEqual(new DateOnly(2027, 1, 1), task.DueDate);
                Assert.IsTrue(task.TagIds.Contains(ReadTagId(root, "Persisted draft tag")));
                ui.InvokeId("open-saved-task", "open saved calendar task");
                ui.AdvanceCalendarDate("task-due-date", "change board calendar date");
                ui.Until(() => ReadBoard(root).Tasks.Single(task => task.Title == "Calendar task").DueDate == new DateOnly(2027, 1, 2), "board calendar commits January 2");
                ui.InvokeId("close-task-details", "close calendar task");

                ui.ExportRecovery(null);
                ui.WaitForName("Recovery export cancelled", "cancel export status");
                string export = Path.Combine(root, "exported-recovery.json");
                ui.ExportRecovery(export);
                ui.WaitForName("Recovery copy exported", "recovery export completed");
                ui.Until(() => File.Exists(export) && JsonSerializer.Deserialize<TaskBoardRecoveryFile>(File.ReadAllText(export)) is not null,
                    "recovery export contains JSON");
                TaskBoardRecoveryFile recovered = JsonSerializer.Deserialize<TaskBoardRecoveryFile>(File.ReadAllText(export))!;
                Assert.AreEqual(new DateOnly(2027, 1, 2), recovered.Document.Tasks.Single(task => task.Title == "Calendar task").DueDate);
                recovered.Document.Tasks.Single(task => task.Title == "Calendar task").Title = "Imported calendar task";
                File.WriteAllText(export, JsonSerializer.Serialize(recovered));
                ui.ImportRecovery(export);
                ui.InvokeDesktopName("Import", "confirm recovery import");
                ui.WaitForName("Recovery copy imported", "recovery import completed");
                ui.WaitForName("Task Imported calendar task, To Do", "imported task restored on board");
            });
        });
    }

    [TestMethod]
    [TestCategory("TaskBoardE2E")]
    public void Real_application_validates_tags_and_removes_active_assignments()
    {
        WithProfile((root, instance, exit) =>
        {
            RunApplication(root, instance, exit, ui =>
            {
                Guid seed = ReadTagId(root, SeedTag);
                ui.InvokeId($"tag-filter-{seed:N}", "activate tag before deleting it");
                ui.InvokeId("board-menu", "open tag manager menu");
                ui.InvokeId("board-menu-manage-tags", "open tag manager");
                ui.InvokeId("add-tag", "reject empty tag");
                ui.WaitForName("Enter a tag name.", "empty tag validation");
                ui.SetValue("new-tag-name", SeedTag, "reject duplicate tag");
                ui.InvokeId("add-tag", "submit duplicate tag");
                ui.WaitForName("A tag with this name already exists.", "duplicate tag validation");
                ui.SetValue("new-tag-name", new string('x', 81), "reject overlong tag");
                ui.InvokeId("add-tag", "submit overlong tag");
                ui.WaitForName("Tag names are limited to 80 characters.", "tag length validation");
                Assert.AreEqual(2, ReadBoard(root).Tags.Count);
                ui.SetValue("new-tag-name", "Keyboard tag", "create tag with Enter");
                ui.PressEnter("submit valid tag with keyboard");
                ui.Until(() => ReadBoard(root).Tags.Any(tag => tag.Name == "Keyboard tag"), "keyboard tag persisted");
                ui.InvokeId($"delete-tag-{seed:N}", "delete assigned active tag");
                ui.InvokeId("tag-delete-confirm", "confirm assigned active tag deletion");
                ui.WaitForGoneId($"tag-name-{seed:N}", "deleted tag row removed");
                ui.InvokeDescendantName("tag-manager-dialog", "Done", "close tag manager");
                ui.ExpectCards("todo", ["Seed task"]);
                TaskBoardDocument board = ReadBoard(root);
                Assert.IsNull(board.View.TagFilter);
                Assert.IsFalse(board.Tags.Any(tag => tag.Id == seed));
                Assert.IsFalse(board.Tasks.Any(task => task.TagIds.Contains(seed)));
            });
            RunApplication(root, instance, exit, ui =>
            {
                ui.ExpectCards("todo", ["Seed task"]);
                ui.InvokeName("Task Seed task, To Do", "open task without deleted tag");
                Assert.AreEqual(0, ReadBoard(root).Tasks.Single().TagIds.Count);
            });
        });
    }

    internal static void WithProfile(Action<string, string, EventWaitHandle> scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinWispr-E2E", Guid.NewGuid().ToString("N"));
        string instance = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\WinWispr-E2E-Exit-{instance}");
        try { SeedProfile(root); scenario(root, instance, exit); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void E2e_startup_settings_and_data_paths_are_isolated()
    {
        WithProfile((root, instance, exit) =>
        {
            string? oldMode = Environment.GetEnvironmentVariable(E2eModeVariable);
            string? oldRoot = Environment.GetEnvironmentVariable(E2eRootVariable);
            string? oldInstance = Environment.GetEnvironmentVariable(E2eInstanceVariable);
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            object? before = key?.GetValue("WinWispr");
            try
            {
                Environment.SetEnvironmentVariable(E2eModeVariable, "taskboard");
                Environment.SetEnvironmentVariable(E2eRootVariable, root);
                Environment.SetEnvironmentVariable(E2eInstanceVariable, instance);
                Assert.IsFalse(StartupManager.IsEnabled());
                Assert.IsTrue(StartupManager.TrySetEnabled(true));
                Assert.IsTrue(StartupManager.IsEnabled());
                Assert.IsTrue(File.Exists(Path.Combine(root, "startup.enabled")));
                Assert.IsTrue(StartupManager.TrySetEnabled(false));
                Assert.IsFalse(StartupManager.IsEnabled());
                Assert.AreEqual(before, key?.GetValue("WinWispr"));
                Environment.SetEnvironmentVariable(E2eRootVariable, Path.GetTempPath());
                Assert.ThrowsExactly<InvalidOperationException>(() => { _ = AppDataPaths.Root; });
                Environment.SetEnvironmentVariable(E2eRootVariable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                Assert.ThrowsExactly<InvalidOperationException>(() => { _ = AppDataPaths.Root; });
                Environment.SetEnvironmentVariable(E2eInstanceVariable, "invalid");
                Assert.ThrowsExactly<InvalidOperationException>(() => { _ = AppDataPaths.E2eInstanceId; });
            }
            finally
            {
                Environment.SetEnvironmentVariable(E2eModeVariable, oldMode);
                Environment.SetEnvironmentVariable(E2eRootVariable, oldRoot);
                Environment.SetEnvironmentVariable(E2eInstanceVariable, oldInstance);
            }
        });
    }

    private static void SeedDraft(string root, string title, DateOnly dueDate)
    {
        string path = Path.Combine(root, "tasks.json");
        var store = new TaskBoardStore(path);
        TaskBoardDocument document = store.Load();
        BoardTag selected = document.Tags.Single(tag => tag.Name == SeedTag);
        BoardTag suggested = document.Tags.Single(tag => tag.Name == SuggestedTag);
        document.View.Search = string.Empty;
        document.View.TagFilter = null;
        document.View.Sort = BoardTaskSort.Manual;
        document.View.ShowArchived = false;
        document.View.NarrowStatus = BoardTaskStatus.ToDo;
        document.Draft = new TaskCaptureDraft
        {
            OriginalTranscript = $"Captured transcript for {title}",
            Description = "Draft description",
            SuggestedTitle = title,
            SuggestedDueDate = dueDate,
            SelectedTagIds = [selected.Id],
            SuggestedTagIds = [suggested.Id]
        };
        store.Save(document);
    }

    private static void SeedStaleRecovery(string root)
    {
        string path = Path.Combine(root, "tasks.json");
        var store = new TaskBoardStore(path);
        TaskBoardDocument document = store.Load();
        BoardTask task = document.Tasks.Single(item => item.Title == "Seed task");
        store.SaveRecovery(document, new Dictionary<Guid, string> { [task.Id] = "Stale recovery" });
        TaskBoardRecoveryFile recovery = JsonSerializer.Deserialize<TaskBoardRecoveryFile>(File.ReadAllText(store.RecoveryPath))!;
        recovery.BaseRevision = Math.Max(0, document.Revision - 1);
        File.WriteAllText(store.RecoveryPath, JsonSerializer.Serialize(recovery));
    }

    private static void SeedRecoverableEdit(string root)
    {
        string path = Path.Combine(root, "tasks.json");
        var store = new TaskBoardStore(path);
        TaskBoardDocument document = store.Load();
        BoardTask task = document.Tasks.Single(item => item.Title == "Seed task");
        store.SaveRecovery(document, new Dictionary<Guid, string> { [task.Id] = RecoveredDescription });
    }

    private static void ExerciseDiscardStaleRecovery(UiDriver ui)
    {
        ui.WaitForName("Task Seed task, To Do", "stale recovery board visible");
        ui.InvokeId("board-menu", "open stale recovery menu");
        ui.InvokeId("board-menu-discard-recovery", "discard stale recovery copy");
    }

    private static void ExerciseAcknowledgeRecoveredData(UiDriver ui)
    {
        ui.WaitForName("Task Seed task, To Do", "recovered task visible");
        ui.InvokeName("Task Seed task, To Do", "open recovered task");
        ui.InvokeName("Edit description", "open recovered description");
        Assert.AreEqual(RecoveredDescription, ui.GetValue("task-description", "read recovered description"));
        ui.PressEscape("close recovered description editor");
        ui.InvokeId("close-task-details", "close recovered task");
        ui.InvokeId("board-menu", "open recovered data menu");
        ui.InvokeId("board-menu-acknowledge-recovery", "acknowledge recovered task data");
        ui.InvokeId("board-menu", "reopen recovered data menu");
        ui.WaitForGoneId("board-menu-acknowledge-recovery", "recovery acknowledgement menu item removed");
    }

    private static void ExerciseDueViewAndDraftDiscard(UiDriver ui)
    {
        ui.WaitForName("Task Seed task, To Do", "due task visible");
        ui.InvokeId("board-menu", "open due-view board menu");
        ui.InvokeId("board-menu-show-all", "restore normal task view");
        ui.Select("task-sort", "Sort: Manual", "restore saved task sort");

        ui.InvokeId("pin-board-on-top", "pin board on top");
        ui.WaitForName("Unpin board from top", "pinned board state");
        ui.InvokeId("pin-board-on-top", "unpin board from top");
        ui.WaitForName("Pin board on top", "unpinned board state");

        UiDriver capture = OpenDraftCapture(ui);
        Assert.AreEqual(
            "Captured transcript for Discardable draft",
            capture.GetValue("captured-task-text", "read captured transcript"));
        capture.WaitForId("capture-task-due-date", "capture due date control");
        capture.InvokeId("capture-cancel", "cancel task capture");
        capture.WaitForWindowGone("New task", "cancelled task capture window");

        capture = OpenDraftCapture(ui);
        capture.InvokeId("capture-discard", "open discard draft confirmation");
        capture.InvokeDescendantName("discard-draft-dialog", "Keep editing", "keep draft after confirmation");
        capture.WaitForId("capture-task-title", "draft remains after keeping it");
        capture.InvokeId("capture-discard", "reopen discard draft confirmation");
        capture.InvokeDescendantName("discard-draft-dialog", "Discard", "confirm discard draft");
        capture.WaitForWindowGone("New task", "discarded task capture window");
    }

    private static void ExerciseTaskCaptureOpen(UiDriver ui, string root, DateOnly expectedDueDate)
    {
        UiDriver capture = OpenDraftCapture(ui);
        ExerciseCaptureEditor(capture, root, CaptureOpenTitle);
        capture.InvokeId("capture-add-task", "add captured task");
        capture.WaitForWindowGone("New task", "saved task capture window");

        ui.WaitForName($"Task {CaptureOpenTitle}, To Do", "captured task on board");
        ui.InvokeId("open-saved-task", "open saved task notification action");
        Assert.AreEqual(CaptureOpenTitle, ui.GetValue("task-title", "read opened captured task title"));
        ui.InvokeId("close-task-details", "close opened captured task");
    }

    private static void ExerciseTaskCaptureUndo(UiDriver ui, string root)
    {
        UiDriver capture = OpenDraftCapture(ui);
        capture.SetValue("capture-task-title", CaptureUndoTitle, "edit undoable capture title");
        capture.SetValue("capture-task-description", "Undo this captured task", "edit undoable capture description");
        capture.WaitForId("capture-task-due-date", "undoable capture due date control");
        capture.InvokeId("capture-add-task", "save undoable captured task");
        capture.WaitForWindowGone("New task", "undoable task capture window");
        ui.WaitForName($"Task {CaptureUndoTitle}, To Do", "undoable task on board");
        ui.InvokeId("undo-saved-task", "undo saved task");
        ui.WaitForGone($"Task {CaptureUndoTitle}, To Do", "undone captured task");
    }

    private static UiDriver OpenDraftCapture(UiDriver ui)
    {
        ui.InvokeId("board-menu", "open board menu for task draft");
        ui.InvokeId("board-menu-resume-draft", "resume task draft");
        return ui.WaitForWindow("New task");
    }

    private static void ExerciseCaptureEditor(UiDriver capture, string root, string title)
    {
        Guid seedTagId = ReadTagId(root, SeedTag);
        Guid suggestedTagId = ReadTagId(root, SuggestedTag);
        Assert.AreEqual(
            "Draft description",
            capture.GetValue("capture-task-description", "read draft description"));
        capture.SetValue("capture-task-title", title, "edit captured task title");
        capture.SetValue("capture-task-description", UpdatedDescription, "edit captured task description");
        capture.WaitForId("capture-task-due-date", "captured task due date control");
        capture.InvokeId($"capture-tag-{seedTagId:N}", "remove selected capture tag");
        capture.InvokeId($"capture-tag-{seedTagId:N}", "restore selected capture tag");
        capture.InvokeId($"capture-suggested-tag-{SuggestedTag}", "accept suggested capture tag");
        capture.InvokeName($"Remove capture tag {SuggestedTag}", "remove accepted suggested capture tag");
        capture.InvokeId($"capture-tag-{suggestedTagId:N}", "add available capture tag");
        capture.SetValue("capture-task-tags", "Capture extra tag", "enter capture tag");
        capture.PressEnter("submit capture tag");
        capture.WaitForName("Remove capture tag Capture extra tag", "capture tag chip");
    }

    private static void ExerciseTaskBoard(UiDriver ui, string root, DateOnly expectedDueDate)
    {
        ui.InvokeId("pin-board-on-top", "pin board before task editing");
        ui.WaitForName("Unpin board from top", "pinned board state before task editing");
        ui.InvokeId("pin-board-on-top", "restore board topmost state");
        ui.WaitForName("Pin board on top", "restored board topmost state");

        ui.InvokeId("add-task", "open and cancel quick task creation");
        ui.SetValue("new-task-title", "Cancelled task", "enter cancelled quick task title");
        ui.PressEscape("cancel quick task creation");
        ui.WaitForGoneId("new-task-title", "cancelled quick task entry");

        ui.InvokeId("add-task", "open task creation");
        ui.SetValue("new-task-title", CreatedTitle, "enter new task title");
        Assert.AreEqual(CreatedTitle, ui.GetValue("new-task-title", "verify new task title before submit"));
        ui.PressEnter("submit new task");
        ui.WaitForName($"Task {CreatedTitle}, To Do", "new task card");

        ui.InvokeName($"Task {CreatedTitle}, To Do", "open new task");
        ui.SetValue("task-title", UpdatedTitle, "edit task title");
        ui.InvokeId("edit-task-description", "open description editor");
        ui.SetValue("task-description", UpdatedDescription, "edit task description");
        ui.WaitForId("task-due-date", "task calendar date picker");
        ui.InvokeName("Set due date today", "set task due date today");
        ui.Until(() => ReadBoard(root).Tasks.Single(task => task.Title == UpdatedTitle).DueDate == DateOnly.FromDateTime(DateTime.Now), "today persisted");
        ui.InvokeName("Set due date no date", "clear task due date");
        ui.Until(() => ReadBoard(root).Tasks.Single(task => task.Title == UpdatedTitle).DueDate is null, "cleared due date persisted");
        ui.InvokeName("Set due date tomorrow", "set task due date tomorrow");
        ui.Until(() => ReadBoard(root).Tasks.Single(task => task.Title == UpdatedTitle).DueDate == expectedDueDate, "tomorrow persisted");
        ui.Select("task-status", "To Do", "select task To Do status");
        ui.Select("task-status", "Doing", "move task to Doing");
        ui.PressEscape("exit description editor");
        ui.InvokeId("close-task-details", "close task details");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Doing", "Doing", "show Doing column in narrow layout");

        ui.InvokeId("board-menu", "open board menu");
        ui.InvokeId("board-menu-manage-tags", "open tag manager");
        ui.SetValue("new-tag-name", CreatedTag, "enter tag name");
        ui.InvokeId("add-tag", "create tag");
        Guid createdTagId = ReadTagId(root, CreatedTag);
        foreach (string color in new[] { "blue", "green", "amber", "purple", "rose" })
        {
            ui.Select($"tag-color-{createdTagId:N}", color, $"select {color} tag color");
            ui.Until(() => ReadBoard(root).Tags.Single(tag => tag.Id == createdTagId).Color == color, $"{color} tag color persisted");
        }
        ui.SetValue($"tag-name-{createdTagId:N}", RenamedTag, "rename tag");
        ui.Select($"tag-color-{createdTagId:N}", "rose", "commit renamed tag");
        ui.SetValue("new-tag-name", TemporaryTag, "create temporary tag");
        ui.InvokeId("add-tag", "add temporary tag");
        Guid temporaryTagId = ReadTagId(root, TemporaryTag);
        ui.InvokeId($"delete-tag-{temporaryTagId:N}", "delete temporary tag");
        ui.InvokeId("tag-delete-cancel", "cancel temporary tag deletion");
        ui.Until(() => ReadBoard(root).Tags.Any(tag => tag.Id == temporaryTagId), "cancelled tag deletion leaves tag intact");
        ui.InvokeId($"delete-tag-{temporaryTagId:N}", "delete temporary tag again");
        ui.InvokeId("tag-delete-confirm", "confirm temporary tag deletion");
        ui.Until(() => !ReadBoard(root).Tags.Any(tag => tag.Id == temporaryTagId), "deleted temporary tag persisted");
        ui.InvokeDescendantName("tag-manager-dialog", "Done", "close tag manager");

        ui.InvokeName($"Task {UpdatedTitle}, Doing", "reopen edited task");
        ui.SetValue("task-tags", RenamedTag, "enter renamed task tag");
        ui.PressEnter("assign task tag");
        ui.WaitForId($"remove-task-tag-{createdTagId:N}", "assigned task tag");
        ui.InvokeId($"remove-task-tag-{createdTagId:N}", "remove task tag");
        ui.WaitForGoneId($"remove-task-tag-{createdTagId:N}", "removed task tag");
        ui.SetValue("task-tags", RenamedTag, "restore task tag");
        ui.PressEnter("restore task tag assignment");
        ui.SetValue("task-tags", SeedTag, "add second task tag");
        ui.PressEnter("assign second task tag");
        ui.SetValue("task-tags", SuggestedTag, "add third task tag");
        ui.PressEnter("assign third task tag");
        ui.WaitForId($"remove-task-tag-{createdTagId:N}", "tagged task card in drawer");
        ui.InvokeId("close-task-details", "close tagged task details");

        AutomationElement taggedCard = ui.WaitForName($"Task {UpdatedTitle}, Doing", "find tagged task card");
        ui.InvokeDescendantId(taggedCard, $"task-tag-{createdTagId:N}", "filter from task card tag");
        ui.InvokeName("Show all tasks", "clear task tag filter");
        ui.InvokeName($"Filter by tag {RenamedTag}", "filter tasks by renamed tag");
        ui.SetValue("task-search", UpdatedTitle, "search for edited task");
        ui.WaitForName($"Task {UpdatedTitle}, Doing", "search result");
        ui.SetValue("task-search", string.Empty, "clear task search");
        ui.WaitForId("task-sort", "wait for search refresh");
        foreach (string sort in new[] { "Sort: Manual", "Title: A–Z", "Title: Z–A", "Due date: Earliest", "Due date: Latest", "Created: Newest", "Created: Oldest" })
        {
            ui.Select("task-sort", sort, $"select {sort} sort");
            ui.WaitForName($"Task {UpdatedTitle}, Doing", $"task visible after {sort} sort");
        }
        ui.Select("task-sort", "Sort: Manual", "restore manual task sort for reorder actions");

        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Doing");
        ui.InvokeId("task-menu-move-todo", "move task to To Do from task menu");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, To Do", "To Do", "show To Do column after menu move");
        OpenTaskMenu(ui, $"Task {UpdatedTitle}, To Do");
        ui.InvokeId("task-menu-move-doing", "move task to Doing from task menu");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Doing", "Doing", "show Doing column after menu move");
        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Doing");
        ui.InvokeId("task-menu-move-done", "move task to Done from task menu");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Done", "Done", "show Done column after menu move");

        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Done");
        ui.InvokeId("task-menu-move-todo", "move task to To Do for reorder test");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, To Do", "To Do", "show To Do column for reorder test");
        OpenTaskMenu(ui, $"Task {UpdatedTitle}, To Do");
        ui.InvokeId("task-menu-move-down", "move task down manually");
        ui.WaitForName($"Task {UpdatedTitle}, To Do", "task after manual move down");
        OpenTaskMenu(ui, $"Task {UpdatedTitle}, To Do");
        ui.InvokeId("task-menu-move-up", "move task up manually");
        ui.WaitForName($"Task {UpdatedTitle}, To Do", "task after manual move up");
        ui.Drag(ui.WaitForName($"Task {UpdatedTitle}, To Do", "drag source task"), ui.WaitForId("task-list-todo", "drag target task list"), "reorder task by drag and drop");
        ui.WaitForName($"Task {UpdatedTitle}, To Do", "task after drag and drop");

        OpenTaskMenu(ui, $"Task {UpdatedTitle}, To Do");
        ui.InvokeId("task-menu-move-done", "move task to Done for archive test");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Done", "Done", "show Done column for archive test");

        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Done");
        ui.InvokeId("task-menu-archive", "archive completed task");
        ui.WaitForGone($"Task {UpdatedTitle}, Done", "archived task hidden");
        ui.Toggle("show-archived", on: true, "show archived tasks");
        ui.WaitForName($"Task {UpdatedTitle}, Archived", "archived task visible");
        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Archived");
        ui.InvokeId("task-menu-restore-archive", "restore archived task");
        ui.WaitForName($"Task {UpdatedTitle}, Done", "restored completed task");
        ui.Toggle("show-archived", on: false, "hide archived tasks");

        ui.InvokeId("board-menu", "open board menu for undo task cleanup");
        ui.InvokeId("board-menu-trash", "open trash for undo task cleanup");
        Guid undoTaskId = ReadBoard(root).Tasks.Single(task => task.Title == CaptureUndoTitle).Id;
        ui.InvokeId($"restore-trash-{undoTaskId:N}", "restore undone captured task");
        ui.InvokeDescendantName("trash-dialog", "Done", "close undo task trash");
        ui.WaitForGoneId("trash-dialog", "undo task trash closed");
        ui.InvokeName("Show all tasks", "clear tag filter before undo task check");
        ui.ShowStatusIfNeeded($"Task {CaptureUndoTitle}, To Do", "To Do", "show restored undo task");
        ui.WaitForName($"Task {CaptureUndoTitle}, To Do", "restored undone captured task");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Done", "Done", "show completed task after trash cleanup");

        ui.ShowStatusIfNeeded($"Task {CaptureUndoTitle}, To Do", "To Do", "show undo task before empty trash test");
        OpenTaskMenu(ui, $"Task {CaptureUndoTitle}, To Do");
        ui.InvokeId("task-menu-trash", "move disposable task to trash");
        ui.InvokeId("board-menu", "open board menu for empty trash action");
        ui.InvokeId("board-menu-trash", "open trash for empty trash action");
        ui.InvokeId("empty-trash", "arm empty trash confirmation");
        ui.WaitForName("Click again to empty trash", "show empty trash confirmation state");
        Assert.IsTrue(ReadBoard(root).Tasks.Any(task => task.Title == CaptureUndoTitle && task.DeletedUtc.HasValue), "first empty trash click should keep the task");
        ui.InvokeId("empty-trash", "confirm empty trash");
        ui.WaitForGoneId("trash-dialog", "trash closes after emptying");
        Assert.IsFalse(ReadBoard(root).Tasks.Any(task => task.Title == CaptureUndoTitle), "confirmed empty trash should remove the task");
        ui.ShowStatusIfNeeded($"Task {UpdatedTitle}, Done", "Done", "show completed task after empty trash");

        OpenTaskMenu(ui, $"Task {UpdatedTitle}, Done");
        ui.InvokeId("task-menu-trash", "move task to trash");
        ui.WaitForGone($"Task {UpdatedTitle}, Done", "task hidden in trash");
        ui.InvokeId("board-menu", "open board menu for trash");
        ui.InvokeId("board-menu-trash", "open trash");
        Guid taskId = ReadBoard(root).Tasks.Single(task => task.Title == UpdatedTitle).Id;
        ui.InvokeId($"restore-trash-{taskId:N}", "restore task from trash");
        ui.WaitForName($"Task {UpdatedTitle}, Done", "restored task visible");
        ui.InvokeDescendantName("trash-dialog", "Done", "close trash");
        ui.WaitForGoneId("trash-dialog", "trash dialog closed");

        ui.InvokeId("board-menu", "open board menu for empty trash");
        ui.InvokeId("board-menu-trash", "open empty trash");
        ui.WaitForId("trash-dialog", "verify empty trash dialog");
        Assert.IsFalse(ReadBoard(root).Tasks.Any(task => task.DeletedUtc.HasValue), "empty trash dialog should contain no deleted tasks");
        ui.InvokeDescendantName("trash-dialog", "Done", "close empty trash");
        ui.Select("task-sort", "Title: A–Z", "persist title task sort before restart");
        ui.InvokeName($"Filter by tag {RenamedTag}", "restore tag filter before restart");

        ui.InvokeName($"Task {UpdatedTitle}, Done", "open task for final UI checks");
        Assert.AreEqual(UpdatedTitle, ui.GetValue("task-title", "read persisted title from UI"));
        ui.InvokeId("close-task-details", "close final task details");
    }

    private static void VerifyRestart(UiDriver ui, string root)
    {
        ui.WaitForName($"Task {UpdatedTitle}, Done", "task after process restart");
        ui.InvokeName($"Task {UpdatedTitle}, Done", "open task after process restart");
        Assert.AreEqual(UpdatedTitle, ui.GetValue("task-title", "verify title after restart"));
        ui.InvokeName("Edit description", "open description after restart");
        Assert.AreEqual(UpdatedDescription, ui.GetValue("task-description", "verify description after restart"));
        Assert.AreEqual("Done", ui.GetSelectionName("task-status", "verify status after restart"));
        ui.InvokeId("close-task-details", "close task after restart");
        ui.InvokeId("close-task-details", "close task drawer after restart description editing");

        TaskBoardDocument document = ReadBoard(root);
        Assert.AreEqual(document.Tags.Single(tag => tag.Name == RenamedTag).Id, document.View.TagFilter);
        Assert.AreEqual(BoardTaskSort.TitleAscending, document.View.Sort);
        Assert.IsFalse(document.View.ShowArchived);
    }

    private static void AssertPersisted(string root, DateOnly expectedDueDate)
    {
        TaskBoardDocument document = ReadBoard(root);
        BoardTag tag = document.Tags.Single(item => item.Name == RenamedTag);
        Assert.AreEqual("rose", tag.Color);
        BoardTask task = document.Tasks.Single(item => item.Title == UpdatedTitle);
        Assert.AreEqual(UpdatedDescription, task.Description);
        Assert.AreEqual(BoardTaskStatus.Done, task.Status);
        Assert.AreEqual(expectedDueDate, task.DueDate);
        Assert.IsTrue(task.TagIds.Contains(tag.Id));
        Assert.IsNull(task.ArchivedUtc);
        Assert.IsNull(task.DeletedUtc);
    }

    private static Guid ReadTagId(string root, string name) =>
        ReadBoard(root).Tags.Single(tag => tag.Name == name).Id;

    private static void OpenTaskMenu(UiDriver ui, string cardName)
    {
        AutomationElement card = ui.WaitForName(cardName, $"find task card {cardName}");
        AutomationElement? menu = card.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
            .Cast<AutomationElement>()
            .FirstOrDefault(element => element.Current.AutomationId.StartsWith("task-actions-", StringComparison.Ordinal));
        menu ??= card.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Task actions"));
        if (menu is null)
            throw new AssertFailedException($"Could not find the task menu button for {cardName}.");
        ui.Invoke(menu, $"open task menu for {cardName}");
    }

    internal static void RunApplication(string root, string instanceId, EventWaitHandle exitEvent, Action<UiDriver> scenario, string launchArgument = "--e2e-taskboard",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "WinWispr.exe");
        Assert.IsTrue(File.Exists(executable), $"The built WinWispr.exe was not found at {executable}.");
        exitEvent.Reset();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(launchArgument);
        startInfo.Environment[E2eModeVariable] = "taskboard";
        startInfo.Environment[E2eRootVariable] = root;
        startInfo.Environment[E2eInstanceVariable] = instanceId;
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
            startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        UiDriver? ui = null;
        Exception? failure = null;
        string? failureTree = null;
        string processState = "unknown";
        bool started = false;
        nint previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object? startupBefore = runKey?.GetValue("WinWispr");
        string strayKeys = UiDriver.ReleaseHeldKeys();
        if (strayKeys.Length > 0)
            Console.WriteLine($"Released keys held down before the E2E start: {strayKeys}");
        try
        {
            started = process.Start();
            Assert.IsTrue(started, "WinWispr.exe did not start.");
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            ui = UiDriver.WaitFor(process, BoardWindowName);
            scenario(ui);
        }
        catch (Exception exception)
        {
            failure = exception;
            failureTree = ui?.DumpTree();
            processState = started ? DescribeProcess(process) : "not started";
        }
        finally
        {
            // A scenario that fails between HoldHotkey and ReleaseHotkey leaves the chord held for the whole
            // desktop session, and every later injected hotkey then carries the extra modifier and matches nothing.
            UiDriver.ReleaseHeldKeys();
            exitEvent.Set();
            // Shutdown steps are bounded by seconds each; a busy machine needs the slack.
            if (started && !process.WaitForExit(60_000))
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
                failure ??= new AssertFailedException("WinWispr.exe did not shut down after the E2E exit signal.");
            }
            if (previousDpi != nint.Zero)
                SetThreadDpiAwarenessContext(previousDpi);
        }

        if (!Equals(startupBefore, runKey?.GetValue("WinWispr")))
            failure ??= new AssertFailedException("The E2E process changed the user's Windows startup registration.");

        string output = ReadOutput(stdout, 5_000);
        string error = ReadOutput(stderr, 5_000);
        if (failure is not null)
        {
            string tree = failureTree ?? ui?.DumpTree() ?? "[UI Automation window was not created]";
            string diagnosticPath = WriteFailureDiagnostic(root, instanceId, failure, tree, output, error);
            throw new AssertFailedException(
                $"Taskboard E2E failed: {failure.Message}{Environment.NewLine}Diagnostic: {diagnosticPath}{Environment.NewLine}{Environment.NewLine}UI Automation tree:{Environment.NewLine}{tree}{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}{Environment.NewLine}application at failure: {processState}{Environment.NewLine}foreground window at failure: {UiDriver.DescribeForeground()}{Environment.NewLine}technical log:{Environment.NewLine}{ReadTechnicalLog(root)}{Environment.NewLine}exception:{Environment.NewLine}{failure}");
        }
        Assert.AreEqual(0, process.ExitCode, $"WinWispr.exe exited with code {process.ExitCode}.{Environment.NewLine}stdout:{output}{Environment.NewLine}stderr:{error}");
    }

    private static string DescribeProcess(Process process)
    {
        try
        {
            return process.HasExited ? $"exited with code {process.ExitCode}" : "still running";
        }
        catch (Exception exception)
        {
            return $"unknown ({exception.Message})";
        }
    }

    // The app's own technical log holds numeric timings and event names only (no audio, transcripts, or
    // window titles), and the profile is deleted after the run, so a failure has to carry it.
    private static string ReadTechnicalLog(string root)
    {
        try
        {
            string directory = Path.Combine(root, "Logs");
            if (!Directory.Exists(directory))
                return "[no technical log]";
            var text = new StringBuilder();
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
                text.AppendLine(Path.GetFileName(file)).AppendLine(File.ReadAllText(file));
            return text.Length > 30_000 ? text.ToString(0, 30_000) + "…" : text.ToString();
        }
        catch (Exception exception)
        {
            return $"[technical log unavailable: {exception.Message}]";
        }
    }

    private static string ReadOutput(Task<string>? output, int timeoutMilliseconds)
    {
        if (output is null || !output.Wait(timeoutMilliseconds))
            return "[output unavailable before timeout]";
        return output.GetAwaiter().GetResult();
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    private static string WriteFailureDiagnostic(string root, string instanceId, Exception failure, string tree, string output, string error)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinWispr-E2E-failures");
        string path = Path.Combine(directory, $"taskboard-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{instanceId}.txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path,
                $"Profile: {root}{Environment.NewLine}Instance: {instanceId}{Environment.NewLine}"
                + $"Failure: {failure}{Environment.NewLine}{Environment.NewLine}"
                + $"UI Automation tree:{Environment.NewLine}{tree}{Environment.NewLine}"
                + $"stdout:{Environment.NewLine}{output}{Environment.NewLine}"
                + $"stderr:{Environment.NewLine}{error}{Environment.NewLine}");
            return path;
        }
        catch (Exception diagnosticFailure)
        {
            return $"[could not write diagnostic: {diagnosticFailure.Message}]";
        }
    }

    internal sealed class UiDriver
    {
        private const int WaitMilliseconds = 30_000;
        // The first start after a build is slow on a busy machine (Defender scans, concurrent builds).
        private const int StartupWaitMilliseconds = 180_000;
        private AutomationElement _window;
        private nint _windowHandle;
        private readonly int _processId;
        private readonly string _windowName;
        private AutomationElement? _lastValueElement;
        private string? _lastValueAutomationId;
        private string _lastStep = "startup";

        // The left-hand modifier keys HoldHotkey injected and ReleaseHotkey has not released yet. A scenario that
        // presses a second hotkey during a held one (translate during a dictation) must not mistake its own chord
        // for a key held on the desktop.
        private static readonly HashSet<ushort> HarnessHeldModifiers = [];
        private UiDriver? _child;

        private UiDriver(AutomationElement window, int processId, string windowName)
        {
            _window = window;
            _windowHandle = window.Current.NativeWindowHandle;
            _processId = processId;
            _windowName = windowName;
        }

        internal static UiDriver WaitFor(Process process, string name)
        {
            Stopwatch timer = Stopwatch.StartNew();
            AutomationElement? window = null;
            while (timer.ElapsedMilliseconds < StartupWaitMilliseconds)
            {
                if (process.HasExited)
                    throw new AssertFailedException($"WinWispr.exe exited before the {name} window appeared (exit code {process.ExitCode}).");
                window = FindNativeWindow(process.Id, name);
                if (window is null)
                    window = AutomationElement.RootElement.FindFirst(
                        TreeScope.Subtree,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                            new PropertyCondition(AutomationElement.NameProperty, name)));
                if (window is not null)
                    break;
                Thread.Sleep(100);
            }
            if (window is null)
                throw new AssertFailedException($"UI Automation timed out after {StartupWaitMilliseconds / 1000} seconds: window {name}. Process windows: {DescribeProcessWindows(process.Id)}");
            return new UiDriver(window, process.Id, name);
        }

        internal UiDriver WaitForWindow(string name)
        {
            Stopwatch timer = Stopwatch.StartNew();
            AutomationElement? window = null;
            while (timer.ElapsedMilliseconds < WaitMilliseconds)
            {
                window = FindNativeWindow(_processId, name);
                if (window is null)
                    window = AutomationElement.RootElement.FindFirst(
                        TreeScope.Subtree,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ProcessIdProperty, _processId),
                            new PropertyCondition(AutomationElement.NameProperty, name)));
                if (window is not null)
                    return _child = new UiDriver(window, _processId, name);
                Thread.Sleep(100);
            }
            throw new AssertFailedException($"UI Automation timed out after {WaitMilliseconds / 1000} seconds: window {name}. Process windows: {DescribeProcessWindows(_processId)}");
        }

        internal void PressHotkey(Hotkey hotkey, string step)
        {
            _lastStep = step;
            TakeForegroundForHotkey(step);
            RequireReleasedModifiers(step);
            var modifiers = new List<ushort>();
            if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0)
                modifiers.Add(0xA2);
            if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0)
                modifiers.Add(0xA4);
            if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0)
                modifiers.Add(0xA0);
            if ((hotkey.Modifiers & HotkeyModifiers.Windows) != 0)
                modifiers.Add(0x5B);
            SendHotkey(modifiers, checked((ushort)hotkey.VirtualKey));
        }

        /// <summary>Presses and holds a hotkey chord until <see cref="ReleaseHotkey"/>.</summary>
        internal void HoldHotkey(Hotkey hotkey, string step)
        {
            _lastStep = step;
            TakeForegroundForHotkey(step);
            RequireReleasedModifiers(step);
            foreach (ushort modifier in ModifierKeys(hotkey))
            {
                SendKeyEvent(modifier, keyUp: false);
                HarnessHeldModifiers.Add(modifier);
                Thread.Sleep(25);
            }
            SendKeyEvent(checked((ushort)hotkey.VirtualKey), keyUp: false);
        }

        internal void ReleaseHotkey(Hotkey hotkey, string step)
        {
            _lastStep = step;
            SendKeyEvent(checked((ushort)hotkey.VirtualKey), keyUp: true);
            List<ushort> modifiers = ModifierKeys(hotkey);
            for (int index = modifiers.Count - 1; index >= 0; index--)
            {
                SendKeyEvent(modifiers[index], keyUp: true);
                HarnessHeldModifiers.Remove(modifiers[index]);
            }
        }

        internal void PressVirtualKey(ushort virtualKey, string step) => PressKey(virtualKey, step);

        /// <summary>Reads the text of the element with this automation id (a text block's name is its text).</summary>
        internal string GetText(string id, string step) => WaitForId(id, step).Current.Name;

        /// <summary>Waits until a window of the app holds the foreground, as the translation preview does when it opens.</summary>
        internal void WaitForOwnForeground(string step) => RequireOwnForeground(step);

        /// <summary>
        /// Sends a key (with an optional modifier held) to whichever window of the app holds the foreground,
        /// without moving the focus to the board.
        /// </summary>
        internal void PressKeyInForeground(ushort virtualKey, string step, ushort? modifier = null)
        {
            _lastStep = step;
            RequireOwnForeground(step);
            PressKeyAnywhere(virtualKey, modifier);
        }

        /// <summary>Sends a key to the foreground window whoever owns it (for example the test's own text target).</summary>
        internal static void PressKeyAnywhere(ushort virtualKey, ushort? modifier = null)
        {
            if (modifier is ushort held)
                SendKeyEvent(held, keyUp: false);
            try
            {
                SendKey(virtualKey);
            }
            finally
            {
                if (modifier is ushort release)
                    SendKeyEvent(release, keyUp: true);
            }
        }


        /// <summary>Null while the app runs; otherwise how it ended.</summary>
        internal string? ExitDescription()
        {
            try
            {
                using Process process = Process.GetProcessById(_processId);
                return process.HasExited ? $"exited with code {process.ExitCode}" : null;
            }
            catch (ArgumentException)
            {
                return "is no longer running";
            }
        }

        internal bool HasWindow(string name) => FindNativeWindow(_processId, name) is not null;

        internal string VisibleWindows() => DescribeProcessWindows(_processId);

        internal List<string> VisibleTitles()
        {
            var titles = new List<string>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint ownerProcessId);
                if (ownerProcessId == _processId && IsWindowVisible(handle))
                    titles.Add(ReadWindowTitle(handle));
                return true;
            }, nint.Zero);
            return titles;
        }

        private static List<ushort> ModifierKeys(Hotkey hotkey)
        {
            var modifiers = new List<ushort>();
            if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0)
                modifiers.Add(0xA2);
            if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0)
                modifiers.Add(0xA4);
            if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0)
                modifiers.Add(0xA0);
            if ((hotkey.Modifiers & HotkeyModifiers.Windows) != 0)
                modifiers.Add(0x5B);
            return modifiers;
        }

        internal void WaitForBoardHidden(string step) => WaitForElement(
            () => FindNativeWindow(_processId, _windowName) is null ? _window : null,
            step);

        internal void WaitForBoardVisible(string step)
        {
            AutomationElement visible = WaitForElement(() => FindNativeWindow(_processId, _windowName), step);
            _window = visible;
            _windowHandle = visible.Current.NativeWindowHandle;
        }

        internal AutomationElement WaitForId(string id, string step)
        {
            _lastStep = step;
            return WaitForElement(() => FindLive(AutomationElement.AutomationIdProperty, id), step);
        }

        internal AutomationElement WaitForName(string name, string step)
        {
            _lastStep = step;
            return WaitForElement(() => FindLive(AutomationElement.NameProperty, name, visibleOnly: true), step);
        }

        internal void InvokeId(string id, string step) => WaitForElement(
            () => TryInvoke(FindLive(AutomationElement.AutomationIdProperty, id), step),
            step);

        /// <summary>Invokes the button with this name, ignoring same-named tooltips and labels.</summary>
        internal void InvokeButton(string name, string step) => WaitForElement(
            () => TryInvoke(FindButtonByName(name), step),
            step);

        private AutomationElement? FindButtonByName(string name)
        {
            try
            {
                System.Windows.Automation.Condition condition = new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                AutomationElement? cached = _window.FindFirst(TreeScope.Descendants, condition);
                if (cached is not null)
                    return cached;
                return RefreshWindow() ? _window.FindFirst(TreeScope.Descendants, condition) : null;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                return null;
            }
        }

        internal void InvokeName(string name, string step) => WaitForElement(
            () => TryInvoke(FindLive(AutomationElement.NameProperty, name), step),
            step);

        internal void InvokeDescendantId(AutomationElement parent, string id, string step)
        {
            AutomationElement child = WaitForElement(
                () => Find(parent, AutomationElement.AutomationIdProperty, id),
                step);
            Invoke(child, step);
        }

        /// <summary>
        /// Waits until the card or its narrow-layout status tab is visible and opens the tab only when the
        /// card is not. The wide layout has no tabs, so a card that is still re-rendering after a filter
        /// change must not be mistaken for a hidden column.
        /// </summary>
        internal void ShowStatusIfNeeded(string cardName, string statusName, string step)
        {
            _lastStep = step;
            string tabName = $"Show {statusName} tasks";
            AutomationElement visible = WaitForElement(
                () => FindLive(AutomationElement.NameProperty, cardName, visibleOnly: true)
                    ?? FindLive(AutomationElement.NameProperty, tabName, visibleOnly: true),
                step);
            if (visible.Current.Name == tabName)
                InvokeName(tabName, step);
        }

        internal void InvokeDescendantName(string parentId, string name, string step)
        {
            AutomationElement parent = WaitForId(parentId, step);
            AutomationElement child = WaitForElement(
                () => Find(parent, AutomationElement.NameProperty, name),
                step);
            Invoke(child, step);
        }

        internal void InvokeDesktopName(string name, string step) => Invoke(WaitForDesktopName(name, step), step);

        internal void Click(AutomationElement element, string step)
        {
            _lastStep = step;
            WaitForElement(() => EnsureVisible(element) ? element : null, step + ": scroll into view");
            Rect bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty)
                throw new AssertFailedException($"{step}: UIA returned an empty click rectangle.");
            TakeForeground(step);
            SendMouseMove(bounds.Left + bounds.Width / 2, bounds.Top + Math.Min(20, bounds.Height / 5));
            Thread.Sleep(100);
            SendMouseButton(leftDown: true);
            try { Thread.Sleep(75); }
            finally { SendMouseButton(leftDown: false); }
        }

        internal void Invoke(AutomationElement element, string step)
        {
            _lastStep = step;
            WaitForElement(() => EnsureVisible(element) ? element : null, step + ": scroll into view");
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
                throw new AssertFailedException($"{step}: UIA element does not support InvokePattern.");
            ((InvokePattern)pattern).Invoke();
        }

        private AutomationElement? TryInvoke(AutomationElement? element, string step)
        {
            if (element is null)
                return null;
            try
            {
                Invoke(element, step);
                return element;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException)
            {
                return null;
            }
        }

        internal void SetValue(string id, string value, string step)
        {
            _lastStep = step;
            AutomationElement element = WaitForElement(() => TryFocusValue(id, step), step);
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                throw new AssertFailedException($"{step}: UIA element does not support ValuePattern.");
            ((ValuePattern)pattern).SetValue(value);
            _lastValueElement = element;
            _lastValueAutomationId = id;
        }

        internal string GetValue(string id, string step)
        {
            AutomationElement element = FindValueElement(WaitForId(id, step), step);
            _lastStep = step;
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                throw new AssertFailedException($"{step}: UIA element does not support ValuePattern.");
            return ((ValuePattern)pattern).Current.Value;
        }

        internal string GetSelectionName(string id, string step)
        {
            AutomationElement combo = WaitForId(id, step);
            if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object pattern))
                throw new AssertFailedException($"{step}: UIA element does not support SelectionPattern.");
            AutomationElement? selected = ((SelectionPattern)pattern).Current.GetSelection().FirstOrDefault();
            return selected?.Current.Name ?? throw new AssertFailedException($"{step}: no selected item was exposed.");
        }

        internal void Select(string id, string itemName, string step)
        {
            AutomationElement combo = WaitForId(id, step);
            _lastStep = step;
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expandPattern))
                ((ExpandCollapsePattern)expandPattern).Expand();
            AutomationElement item = WaitForDesktopName(itemName, step);
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selectionPattern))
                ((SelectionItemPattern)selectionPattern).Select();
            else
                Invoke(item, step);
            WaitForElement(() =>
            {
                if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object currentPattern))
                    return null;
                return ((SelectionPattern)currentPattern).Current.GetSelection()
                    .FirstOrDefault(selected => selected.Current.Name == itemName);
            }, $"{step}: selected item {itemName}");
        }

        internal void Toggle(string id, bool on, string step)
        {
            AutomationElement element = WaitForId(id, step);
            _lastStep = step;
            if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out object pattern))
                throw new AssertFailedException($"{step}: UIA element does not support TogglePattern.");
            var toggle = (TogglePattern)pattern;
            if ((toggle.Current.ToggleState == ToggleState.On) != on)
                toggle.Toggle();
            WaitForElement(() =>
            {
                if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out object currentPattern))
                    return null;
                return ((TogglePattern)currentPattern).Current.ToggleState == (on ? ToggleState.On : ToggleState.Off)
                    ? element
                    : null;
            }, $"{step}: toggle state {(on ? "on" : "off")}");
        }

        internal void PressEnter(string step)
        {
            PressKey(0x0D, step);
        }

        internal void PressEscape(string step)
        {
            PressKey(0x1B, step);
        }

        internal void Drag(AutomationElement source, AutomationElement target, string step, bool cancel = false)
        {
            _lastStep = step;
            Rect sourceBounds = source.Current.BoundingRectangle;
            Rect targetBounds = target.Current.BoundingRectangle;
            if (sourceBounds.IsEmpty || targetBounds.IsEmpty)
                throw new AssertFailedException($"{step}: UIA returned an empty drag rectangle.");
            TakeForeground(step);
            // Start on the card surface, above its nested tag and menu buttons.
            DragPoints(sourceBounds.Left + sourceBounds.Width / 2, sourceBounds.Top + Math.Min(20, sourceBounds.Height / 5),
                targetBounds.Left + targetBounds.Width / 2, targetBounds.Top + Math.Min(20, targetBounds.Height / 5), cancel);
        }

        private static void DragPoints(double x, double y, double targetX, double targetY, bool cancel = false)
        {
            SendMouseMove(x, y);
            Thread.Sleep(100);
            SendMouseButton(leftDown: true);
            try
            {
                Thread.Sleep(150);
                for (int index = 1; index <= 12; index++)
                {
                    SendMouseMove(x + (targetX - x) * index / 12, y + (targetY - y) * index / 12);
                    Thread.Sleep(30);
                }
                Thread.Sleep(250);
                if (cancel)
                    SendKey(0x1B);
            }
            finally { SendMouseButton(leftDown: false); }
        }

        internal void DragBy(string id, double delta, string step)
        {
            _lastStep = step;
            Rect bounds = WaitForId(id, step).Current.BoundingRectangle;
            Assert.IsFalse(bounds.IsEmpty, step);
            TakeForeground(step);
            DragPoints(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2,
                bounds.Left + bounds.Width / 2 + delta, bounds.Top + bounds.Height / 2);
        }

        internal void ResizeWindow(double width, double height, string step)
        {
            _lastStep = step;
            RefreshWindow();
            double scale = GetDpiForWindow(_windowHandle) / 96d;
            width *= scale;
            height *= scale;
            Assert.IsTrue(_window.TryGetCurrentPattern(TransformPattern.Pattern, out object pattern), "Window must support UIA resize.");
            var transform = (TransformPattern)pattern;
            Assert.IsTrue(transform.Current.CanResize, "Window must be resizable.");
            if (transform.Current.CanMove)
                transform.Move(0, 0);
            transform.Resize(width, height);
            Until(() => Math.Abs(_window.Current.BoundingRectangle.Width - width) < 5,
                $"{step}: requested {width}x{height}; initial bounds {_window.Current.BoundingRectangle}");
        }

        internal void AdvanceCalendarDate(string id, string step)
        {
            _lastStep = step;
            InvokeId(id, step);
            AutomationElement calendar = WaitForElement(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _processId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Calendar),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false))), step + ": calendar visible");
            TakeForeground(step);
            if (calendar.TryGetCurrentPattern(SelectionPattern.Pattern, out object selection))
                ((SelectionPattern)selection).Current.GetSelection().FirstOrDefault()?.SetFocus();
            RequireOwnForeground(step);
            SendKey(0x27);
            SendKey(0x0D);
            Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _processId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Calendar),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false))) is null, step + ": calendar closed");
        }

        internal void ExportRecovery(string? fileName)
        {
            InvokeId("board-menu", "open recovery export menu");
            InvokeId("board-menu-export-recovery", "open recovery save dialog");
            AutomationElement dialog = WaitForElement(() => _window.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>().FirstOrDefault(candidate => !candidate.Current.IsOffscreen
                    && Find(candidate, AutomationElement.AutomationIdProperty, "FileNameControlHost") is not null), "native save dialog owned by the board");
            if (fileName is null)
            {
                Invoke(DialogButton(dialog, "2"), "cancel recovery export");
                return;
            }
            AutomationElement input = WaitForElement(() => dialog.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))), "save dialog filename");
            var value = (ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern);
            input.SetFocus();
            value.SetValue(string.Empty);
            RequireDialogForeground(dialog, "type recovery filename");
            foreach (char character in fileName)
            {
                INPUT[] keys =
                [
                    new() { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { ScanCode = character, Flags = 4 } } },
                    new() { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { ScanCode = character, Flags = 6 } } }
                ];
                Assert.AreEqual(2u, SendInput(2, keys, Marshal.SizeOf<INPUT>()), "Type recovery filename");
            }
            Until(() => value.Current.Value == fileName, "typed recovery destination");
            Assert.AreEqual(fileName, value.Current.Value, "Recovery export destination");
            _lastStep = "confirm recovery filename with Enter";
            input.SetFocus();
            RequireDialogForeground(dialog, _lastStep);
            SendKey(0x0D);
        }

        internal void ImportRecovery(string fileName)
        {
            InvokeId("board-menu", "open recovery import menu");
            InvokeId("board-menu-import-recovery", "open recovery open dialog");
            AutomationElement dialog = WaitForElement(() => _window.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>().FirstOrDefault(candidate => !candidate.Current.IsOffscreen
                    && Find(candidate, AutomationElement.AutomationIdProperty, "1148") is not null), "native open dialog owned by the board");
            AutomationElement input = WaitForElement(() => dialog.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))), "open dialog filename");
            if (!input.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                throw new AssertFailedException("Open dialog filename does not support ValuePattern.");
            input.SetFocus();
            ((ValuePattern)pattern).SetValue(fileName);
            RequireDialogForeground(dialog, "confirm recovery import filename with Enter");
            SendKey(0x0D);
        }

        private static AutomationElement DialogButton(AutomationElement dialog, string id) =>
            dialog.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)))
            ?? throw new AssertFailedException($"Native save dialog button {id} was not found.");

        internal void Until(Func<bool> condition, string step)
        {
            _lastStep = step;
            WaitForElement(() =>
            {
                try { return condition() ? this : null; }
                catch (Exception exception) when (exception is IOException or JsonException) { return null; }
            }, step);
        }

        internal void ExpectCards(string status, string[] titles)
        {
            string step = $"{status} cards must be [{string.Join(", ", titles)}]";
            Stopwatch stable = Stopwatch.StartNew();
            string[] actual = [];
            Until(() =>
            {
                AutomationElement list = WaitForId("task-list-" + status, step);
                actual = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>()
                    .Where(item => item.Current.AutomationId.StartsWith("task-", StringComparison.Ordinal)
                        && Guid.TryParse(item.Current.AutomationId[5..], out _) && !item.Current.IsOffscreen)
                    .OrderBy(item => item.Current.BoundingRectangle.Top)
                    .Select(item => item.Current.Name).ToArray();
                string label = status == "todo" ? "To Do" : status == "doing" ? "Doing" : "Done";
                if (!actual.SequenceEqual(titles.Select(title => $"Task {title}, {label}")))
                    stable.Restart();
                return stable.ElapsedMilliseconds >= 400;
            }, step);
        }

        internal void ExpectHiddenId(string id, string step) => Until(() =>
        {
            Assert.IsTrue(RefreshWindow(), "The application window must remain available.");
            AutomationElement? element = _window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            return element is null || element.Current.IsOffscreen;
        }, step);

        internal void WaitForGone(string name, string step)
        {
            WaitForAbsent(AutomationElement.NameProperty, name, step);
        }

        internal void WaitForGoneId(string id, string step)
        {
            WaitForAbsent(AutomationElement.AutomationIdProperty, id, step);
        }

        private void WaitForAbsent(AutomationProperty property, string value, string step)
        {
            _lastStep = step;
            Stopwatch stable = Stopwatch.StartNew();
            WaitForElement(() =>
            {
                try
                {
                    if (!RefreshWindow())
                        throw new AssertFailedException($"{step}: the application window disappeared.");
                    if (_window.FindFirst(TreeScope.Descendants, new PropertyCondition(property, value)) is not null)
                        stable.Restart();
                    return stable.ElapsedMilliseconds >= 400 ? _window : null;
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
                {
                    stable.Restart();
                    return null;
                }
            }, step);
        }

        internal void WaitForWindowGone(string name, string step)
        {
            WaitForElement(() => FindNativeWindow(_processId, name) is null ? _window : null, step);
        }

        internal string DumpTree()
        {
            var output = new StringBuilder();
            output.AppendLine($"Last step: {_lastStep}");
            try
            {
                Rect bounds = _window.Current.BoundingRectangle;
                if (!bounds.IsEmpty && !_window.Current.IsOffscreen)
                {
                    string directory = Path.Combine(Path.GetTempPath(), "WinWispr-E2E-failures");
                    Directory.CreateDirectory(directory);
                    string screenshot = Path.Combine(directory, $"window-{_processId}-{Guid.NewGuid():N}.png");
                    using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen((int)bounds.X, (int)bounds.Y, 0, 0, bitmap.Size);
                    bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
                    output.AppendLine($"Screenshot: {screenshot}");
                }
            }
            catch (Exception exception)
            {
                output.AppendLine($"[screenshot failed: {exception.Message}]");
            }
            try
            {
                Dump(AutomationElement.FromHandle(_windowHandle), output, 0);
            }
            catch (Exception exception)
            {
                output.AppendLine($"[tree dump failed: {exception.Message}]");
            }
            if (_child is not null)
                output.AppendLine(_child.DumpTree());
            return output.ToString();
        }

        private AutomationElement WaitForDesktopName(string name, string step) =>
            WaitForElement(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, _processId),
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false))), step);

        private static AutomationElement FindValueElement(AutomationElement element, string step)
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                return element;
            AutomationElement? child = element.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(candidate => candidate.TryGetCurrentPattern(ValuePattern.Pattern, out _));
            return child ?? throw new AssertFailedException($"{step}: UIA element does not expose an editable ValuePattern.");
        }

        private AutomationElement? TryFocusValue(string id, string step)
        {
            try
            {
                AutomationElement? container = FindLive(AutomationElement.AutomationIdProperty, id);
                if (container is null)
                    return null;
                AutomationElement element = FindValueElement(container, step);
                FocusWindow();
                return TryFocus(element) ? element : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                return null;
            }
        }

        private static bool TryFocus(AutomationElement element)
        {
            try
            {
                if (!EnsureVisible(element))
                    return false;
                element.SetFocus();
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                return false;
            }
        }

        private static bool EnsureVisible(AutomationElement element)
        {
            Rect bounds = element.Current.BoundingRectangle;
            Rect screen = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!element.Current.IsOffscreen || (!bounds.IsEmpty && bounds.IntersectsWith(screen)))
                return true;
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object item))
            {
                ((ScrollItemPattern)item).ScrollIntoView();
                return !element.Current.IsOffscreen;
            }
            for (AutomationElement? parent = TreeWalker.ControlViewWalker.GetParent(element);
                 parent is not null; parent = TreeWalker.ControlViewWalker.GetParent(parent))
            {
                if (!parent.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern))
                    continue;
                var scroll = (ScrollPattern)pattern;
                if (!scroll.Current.VerticallyScrollable)
                    continue;
                bool above = element.Current.BoundingRectangle.Top < parent.Current.BoundingRectangle.Top;
                scroll.Scroll(ScrollAmount.NoAmount, above ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement);
                return false;
            }
            return false;
        }

        private static AutomationElement? Find(AutomationElement root, AutomationProperty property, string value, bool visibleOnly = false)
        {
            try
            {
                System.Windows.Automation.Condition condition = new PropertyCondition(property, value);
                if (visibleOnly)
                    condition = new AndCondition(condition, new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
                return root.FindFirst(TreeScope.Descendants, condition);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                return null;
            }
        }

        private AutomationElement? FindLive(AutomationProperty property, string value, bool visibleOnly = false)
        {
            try
            {
                AutomationElement? cached = Find(_window, property, value, visibleOnly);
                if (cached is not null)
                    return cached;
                if (!RefreshWindow())
                    return null;
                return Find(_window, property, value, visibleOnly);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                return null;
            }
        }

        private bool RefreshWindow()
        {
            AutomationElement? fresh = FindNativeWindow(_processId, _windowName);
            if (fresh is null)
                return false;
            _window = fresh;
            _windowHandle = fresh.Current.NativeWindowHandle;
            return true;
        }

        private static string DescribeProcessWindows(int processId)
        {
            try
            {
                var windows = new List<string>();
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint ownerProcessId);
                    if (ownerProcessId != processId || !IsWindowVisible(handle))
                        return true;
                    windows.Add($"'{ReadWindowTitle(handle)}' hwnd=0x{handle.ToInt64():X}");
                    return true;
                }, nint.Zero);
                return windows.Count == 0 ? "[no visible HWND]" : string.Join(", ", windows);
            }
            catch (Exception exception)
            {
                return $"[window enumeration failed: {exception.Message}]";
            }
        }

        private static AutomationElement? FindNativeWindow(int processId, string expectedName)
        {
            nint match = nint.Zero;
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint ownerProcessId);
                if (ownerProcessId == processId && IsWindowVisible(handle)
                    && string.Equals(ReadWindowTitle(handle), expectedName, StringComparison.Ordinal))
                {
                    match = handle;
                    return false;
                }
                return true;
            }, nint.Zero);
            if (match == nint.Zero)
                return null;
            try
            {
                return AutomationElement.FromHandle(match);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException)
            {
                return null;
            }
        }

        // Injected keys and mouse input reach only the foreground window, and an elevated window in front (Task
        // Manager, an administrator console) swallows them entirely, global hotkeys included. Every injecting step
        // therefore brings the app's window to the front first and fails at once, naming the window in front,
        // instead of timing out 30 s later on a UI state that never comes.
        private void TakeForeground(string step) =>
            WaitForForeground(step, focusApp: true, ForegroundIsOwn);

        // A global hotkey reaches the app from any foreground window it is not blocked from. While the app shows
        // no window (for example the hidden board), only a window that outranks the test can hold the hotkey back.
        private void TakeForegroundForHotkey(string step)
        {
            if (FindNativeWindow(_processId, _windowName) is not null)
                TakeForeground(step);
            else
                WaitForForeground(step, focusApp: false, () => !ForegroundOutranksTest());
        }

        // A modifier held on the desktop (someone typing on the VM, or a key the VM host left down) joins the
        // injected chord, so Windows matches no registered hotkey and the app never reacts. Fail at once and name
        // the held keys instead of timing out 30 s later on a UI state that never comes.
        private static void RequireReleasedModifiers(string step)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                string[] held = HeldModifiers();
                if (held.Length == 0)
                    return;
                if (timer.ElapsedMilliseconds > 2_000)
                    throw new AssertFailedException(
                        $"{step}: {string.Join(", ", held)} held down on the desktop, so the injected hotkey would not "
                        + $"match. {DescribeForeground()} holds the foreground. The E2E needs an undisturbed "
                        + "interactive desktop; release the keys and rerun.");
                Thread.Sleep(100);
            }
        }

        // Side-specific keys, so a modifier the harness holds through HoldHotkey (always the left key) is exempt
        // while the same modifier on the right-hand side still counts as held on the desktop.
        private static string[] HeldModifiers() =>
            new (ushort Key, string Name)[]
            {
                (0xA0, "left Shift"), (0xA1, "right Shift"), (0xA2, "left Ctrl"), (0xA3, "right Ctrl"),
                (0xA4, "left Alt"), (0xA5, "right Alt"), (0x5B, "left Windows"), (0x5C, "right Windows")
            }
                .Where(modifier => !HarnessHeldModifiers.Contains(modifier.Key))
                .Where(modifier => (GetAsyncKeyState(modifier.Key) & 0x8000) != 0)
                .Select(modifier => modifier.Name)
                .ToArray();

        // For steps that move the focus to the app's own popup or dialog before they send keys.
        private void RequireOwnForeground(string step) =>
            WaitForForeground(step, focusApp: false, ForegroundIsOwn);

        // Windows 11 hosts the app's file dialogs in a separate process (PickerHost), so the dialog's own process
        // counts as the app's.
        private void RequireDialogForeground(AutomationElement dialog, string step)
        {
            uint dialogProcess = (uint)dialog.Current.ProcessId;
            nint dialogWindow = dialog.Current.NativeWindowHandle;
            WaitForForeground(step, focusApp: false, () =>
            {
                nint foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint owner);
                return foreground == dialogWindow || owner == dialogProcess || ForegroundIsOwn();
            });
        }

        private void WaitForForeground(string step, bool focusApp, Func<bool> acceptable)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                if (focusApp)
                    FocusWindow();
                if (acceptable())
                    return;
                if (timer.ElapsedMilliseconds > 2_000)
                    throw new AssertFailedException(
                        $"{step}: {DescribeForeground()} holds the foreground, so injected input would not reach "
                        + "WinWispr. The E2E needs an undisturbed interactive desktop; close that window and rerun.");
                Thread.Sleep(100);
            }
        }

        private bool ForegroundIsOwn()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
            return owner == (uint)_processId || owner == (uint)Environment.ProcessId;
        }

        private static bool ForegroundOutranksTest()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
            if (owner == 0)
                return false;
            int? theirs = IntegrityLevel(owner);
            // A process whose level cannot be read is treated as outranking: it is protected or elevated.
            return theirs is null || theirs > (IntegrityLevel((uint)Environment.ProcessId) ?? MediumIntegrity);
        }

        internal static string DescribeForeground()
        {
            nint handle = GetForegroundWindow();
            if (handle == nint.Zero)
                return "no foreground window";
            GetWindowThreadProcessId(handle, out uint processId);
            string process;
            try
            {
                using Process owner = Process.GetProcessById((int)processId);
                process = owner.ProcessName;
            }
            catch (Exception)
            {
                process = "unknown process";
            }

            string integrity = IntegrityLevel(processId) switch
            {
                null => "integrity unknown",
                >= SystemIntegrity => "system integrity",
                >= HighIntegrity => "elevated",
                >= MediumIntegrity => "not elevated",
                _ => "low integrity"
            };
            return $"'{ReadWindowTitle(handle)}' of {process} (pid {processId}, {integrity})";
        }

        private const int MediumIntegrity = 0x2000;
        private const int HighIntegrity = 0x3000;
        private const int SystemIntegrity = 0x4000;

        private static int? IntegrityLevel(uint processId)
        {
            const uint QueryLimitedInformation = 0x1000;
            const uint TokenQuery = 0x0008;
            const int TokenIntegrityLevel = 25;
            nint process = OpenProcess(QueryLimitedInformation, false, processId);
            if (process == nint.Zero)
                return null;
            try
            {
                if (!OpenProcessToken(process, TokenQuery, out nint token))
                    return null;
                try
                {
                    _ = GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out int length);
                    if (length <= 0)
                        return null;
                    nint buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                            return null;
                        nint sid = Marshal.ReadIntPtr(buffer);
                        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    _ = CloseHandle(token);
                }
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }

        private static string ReadWindowTitle(nint handle)
        {
            int length = GetWindowTextLength(handle);
            var title = new StringBuilder(length + 1);
            _ = GetWindowText(handle, title, title.Capacity);
            return title.ToString();
        }

        private void FocusWindow()
        {
            nint handle = _windowHandle;
            uint targetThread = GetWindowThreadProcessId(handle, out _);
            uint currentThread = GetCurrentThreadId();
            nint foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            bool attachedToForeground = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedToTarget = targetThread != 0 && targetThread != currentThread
                && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                _ = ShowWindow(handle, 9);
                _ = BringWindowToTop(handle);
                _ = SetForegroundWindow(handle);
            }
            finally
            {
                if (attachedToTarget)
                    _ = AttachThreadInput(currentThread, targetThread, false);
                if (attachedToForeground)
                    _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        private static T WaitForElement<T>(Func<T?> probe, string step) where T : class
        {
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                T? result = null;
                try { result = probe(); }
                catch (Exception exception) when (exception is ElementNotAvailableException or COMException) { }
                if (result is not null)
                    return result;
                Thread.Sleep(100);
            }
            while (timer.ElapsedMilliseconds < WaitMilliseconds);
            // Name the desktop state in the first line: a foreign foreground window or a held key is the usual cause.
            string[] held = HeldModifiers();
            throw new AssertFailedException(
                $"UI Automation timed out after {WaitMilliseconds / 1000} seconds: {step}. "
                + $"Foreground: {DescribeForeground()}. Held modifiers: {(held.Length == 0 ? "none" : string.Join(", ", held))}.");
        }

        private static void Dump(AutomationElement element, StringBuilder output, int depth)
        {
            if (depth > 5)
                return;
            AutomationElement.AutomationElementInformation current = element.Current;
            output.Append(' ', depth * 2)
                .Append(current.ControlType.ProgrammaticName)
                .Append(" name=").Append(current.Name)
                .Append(" id=").Append(current.AutomationId)
                .Append(" offscreen=").Append(current.IsOffscreen)
                .Append(" bounds=").Append(current.BoundingRectangle)
                .AppendLine();
            foreach (AutomationElement child in element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
                Dump(child, output, depth + 1);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint handle);

        private delegate bool EnumWindowsCallback(nint handle, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(nint handle);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint handle);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint firstThreadId, uint secondThreadId, bool attach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint handle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint handle, int command);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(nint process, uint access, out nint token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern nint GetSidSubAuthorityCount(nint sid);

        [DllImport("advapi32.dll")]
        private static extern nint GetSidSubAuthority(nint sid, uint index);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct INPUT
        {
            [FieldOffset(0)]
            internal uint Type;

            [FieldOffset(8)]
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            internal KEYBDINPUT Keyboard;

            [FieldOffset(0)]
            internal MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            internal int Dx;
            internal int Dy;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal nint ExtraInfo;
        }

        private static void SendKey(ushort virtualKey)
        {
            var inputs = new[]
            {
                new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey } } },
                new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = 2 } } }
            };
            if (SendInput(1, [inputs[0]], Marshal.SizeOf<INPUT>()) != 1)
                throw new AssertFailedException($"SendInput key down failed for virtual key 0x{virtualKey:X2}.");
            try { Thread.Sleep(100); }
            finally
            {
                if (SendInput(1, [inputs[1]], Marshal.SizeOf<INPUT>()) != 1)
                    throw new AssertFailedException($"SendInput key up failed for virtual key 0x{virtualKey:X2}.");
            }
        }

        private void PressKey(ushort virtualKey, string step)
        {
            _lastStep = step;
            TakeForeground(step);
            if (_lastValueAutomationId is not null)
                _lastValueElement = WaitForElement(
                    () => TryFocusValue(_lastValueAutomationId, step),
                    step);
            else if (_lastValueElement is not null && !TryFocus(_lastValueElement))
                throw new AssertFailedException($"{step}: UIA value element could not receive focus.");
            RequireOwnForeground(step);
            SendKey(virtualKey);
        }

        private static void SendMouseMove(double x, double y)
        {
            int left = GetSystemMetrics(76);
            int top = GetSystemMetrics(77);
            int width = Math.Max(1, GetSystemMetrics(78) - 1);
            int height = Math.Max(1, GetSystemMetrics(79) - 1);
            var input = new INPUT
            {
                Type = 0,
                Data = new InputUnion
                {
                    Mouse = new MOUSEINPUT
                    {
                        Dx = (int)Math.Clamp((x - left) * 65535 / width, 0, 65535),
                        Dy = (int)Math.Clamp((y - top) * 65535 / height, 0, 65535),
                        Flags = 0x0001 | 0x8000 | 0x4000
                    }
                }
            };
            SendMouse(input);
        }

        private static void SendMouseButton(bool leftDown)
        {
            var input = new INPUT
            {
                Type = 0,
                Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = leftDown ? 0x0002u : 0x0004u } }
            };
            SendMouse(input);
        }

        private static void SendMouse(INPUT input)
        {
            if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
                throw new AssertFailedException("SendInput failed for mouse input.");
        }

        /// <summary>
        /// Sends a key up for every keyboard key Windows reports as held and returns their virtual keys. Injected
        /// key downs without key ups (a failed or killed run) stay held until a key up arrives; a held Shift turns
        /// every injected Ctrl+Alt hotkey into Ctrl+Alt+Shift, which no registered hotkey matches.
        /// </summary>
        internal static string ReleaseHeldKeys()
        {
            var released = new List<string>();
            // 0x01 to 0x06 are mouse buttons.
            for (int virtualKey = 0x08; virtualKey <= 0xFE; virtualKey++)
            {
                if ((GetAsyncKeyState(virtualKey) & 0x8000) == 0)
                    continue;
                SendKeyEvent((ushort)virtualKey, keyUp: true);
                released.Add($"0x{virtualKey:X2}");
            }
            HarnessHeldModifiers.Clear();
            return string.Join(' ', released);
        }

        private static void SendHotkey(IReadOnlyList<ushort> modifiers, ushort virtualKey)
        {
            foreach (ushort modifier in modifiers)
            {
                SendKeyEvent(modifier, keyUp: false);
                Thread.Sleep(25);
            }
            SendKeyEvent(virtualKey, keyUp: false);
            Thread.Sleep(100);
            SendKeyEvent(virtualKey, keyUp: true);
            for (int index = modifiers.Count - 1; index >= 0; index--)
                SendKeyEvent(modifiers[index], keyUp: true);
        }

        private static void SendKeyEvent(ushort virtualKey, bool keyUp)
        {
            var input = new INPUT
            {
                Type = 1,
                Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = keyUp ? 2u : 0u } }
            };
            if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
                throw new AssertFailedException($"SendInput hotkey failed for virtual key 0x{virtualKey:X2}.");
        }
    }
}
