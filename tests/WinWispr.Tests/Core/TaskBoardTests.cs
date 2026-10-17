using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class TaskBoardTests
{
    [TestMethod]
    public void Saving_a_new_task_keeps_other_pending_edits_and_draft_recoverable()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            BoardTask original = TaskBoardOperations.CreateTask(document, "Existing", "saved", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            string pending = new('x', 100_001);
            document.Draft = new TaskCaptureDraft { SuggestedTitle = "Unfinished capture" };
            TaskBoardOperations.CreateTask(document, "Quick task", "", "", null, [], DateTimeOffset.UtcNow);
            TaskBoardSaveResult result = store.SaveWithRecovery(document.Copy(),
                new() { [original.Id] = pending }, new(), null, true);
            Assert.IsTrue(result.PrimarySaved);
            Assert.IsNull(result.Error);
            var reopened = new TaskBoardStore(path);
            TaskBoardDocument restored = reopened.Load();
            Assert.AreEqual(2, restored.Tasks.Count);
            Assert.AreEqual(pending, reopened.RecoveryPendingDescriptions[original.Id]);
            Assert.AreEqual("Unfinished capture", restored.Draft?.SuggestedTitle);
            Assert.IsNull(reopened.RecoveryPendingQuickEntry);
        });
    }

    [TestMethod]
    public void Recovery_export_identity_tracks_metadata_and_invalid_inputs_but_not_save_revision()
    {
        var document = new TaskBoardDocument();
        BoardTask task = TaskBoardOperations.CreateTask(document, "Original", "", "", null, [], DateTimeOffset.UtcNow);
        var descriptions = new Dictionary<Guid, string>();
        var titles = new Dictionary<Guid, string>();
        string Identity(string? quick = null) => TaskBoardStore.RecoveryContentIdentity(document, descriptions, titles, quick);
        string exported = Identity();
        document.Revision++;
        Assert.AreEqual(exported, Identity());
        task.Status = BoardTaskStatus.Doing;
        Assert.AreNotEqual(exported, Identity());
        task.Status = BoardTaskStatus.ToDo;
        titles[task.Id] = "";
        Assert.AreNotEqual(exported, Identity());
        titles.Clear();
        descriptions[task.Id] = new string('x', 100_001);
        Assert.AreNotEqual(exported, Identity());
        descriptions.Clear();
        Assert.AreNotEqual(exported, Identity("unsubmitted input"));
    }

    [TestMethod]
    public void Damaged_recovery_beside_valid_primary_requires_a_decision_before_replacement()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            store.Save(document);
            const string damaged = "{damaged but irreplaceable";
            File.WriteAllText(store.RecoveryPath, damaged);
            var reopened = new TaskBoardStore(path);
            document = reopened.Load();
            Assert.IsTrue(reopened.CanSave);
            Assert.IsTrue(reopened.RecoveryNeedsChoice);
            Assert.ThrowsExactly<IOException>(() => reopened.SaveRecovery(document, new Dictionary<Guid, string>()));
            Assert.AreEqual(damaged, File.ReadAllText(store.RecoveryPath));
        });
    }

    [TestMethod]
    public void Recovery_import_backup_allows_a_fresh_profile_without_a_primary_file()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            Assert.IsNull(store.CreateRecoveryImportBackup());
            Assert.IsFalse(File.Exists(path));
        });
    }

    [TestMethod]
    public void Recovery_copy_can_be_restored_after_a_failed_primary_write()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            TaskBoardOperations.CreateTask(document, "Before", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            store.SaveRecovery(document, new Dictionary<Guid, string>());
            byte[] originalRecovery = store.CaptureRecoveryCopy()!;

            document.Tasks[0].Status = BoardTaskStatus.Doing;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                _ = store.SaveWithRecovery(document.Copy(), new(), new(), null, true);

            store.RestoreRecoveryCopy(originalRecovery);
            CollectionAssert.AreEqual(originalRecovery, File.ReadAllBytes(store.RecoveryPath));
        });
    }

    [TestMethod]
    public void Snapshot_does_not_observe_later_UI_changes()
    {
        var document = new TaskBoardDocument();
        BoardTag tag = AddTag(document, "Before");
        TaskBoardOperations.CreateTask(document, "Before", "", "", null, [tag.Id], DateTimeOffset.UtcNow);
        document.Draft = new TaskCaptureDraft { SelectedTagIds = [tag.Id], ExplicitTagNames = ["Draft tag"] };
        TaskBoardDocument snapshot = document.Copy();
        document.Tasks[0].Title = "After";
        document.Tasks[0].TagIds.Clear();
        tag.Name = "After";
        document.View.Search = "After";
        document.Draft.SelectedTagIds.Clear();
        document.Draft.ExplicitTagNames.Clear();
        Assert.AreEqual("Before", snapshot.Tasks[0].Title);
        Assert.AreEqual(1, snapshot.Tasks[0].TagIds.Count);
        Assert.AreEqual("Before", snapshot.Tags[0].Name);
        Assert.AreEqual("", snapshot.View.Search);
        Assert.AreEqual(1, snapshot.Draft!.SelectedTagIds.Count);
        Assert.AreEqual(1, snapshot.Draft.ExplicitTagNames.Count);
    }

    [TestMethod]
    public void Failed_primary_metadata_save_writes_recovery_without_text_buffers()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            BoardTask task = TaskBoardOperations.CreateTask(document, "Before", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            task.Status = BoardTaskStatus.Doing;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                TaskBoardSaveResult result = store.SaveWithRecovery(document.Copy(), new(), new(), null, true);
                Assert.IsFalse(result.PrimarySaved);
                Assert.IsTrue(result.RecoverySaved);
            }
            Assert.AreEqual(BoardTaskStatus.Doing, new TaskBoardStore(path).Load().Tasks.Single().Status);
        });
    }

    [TestMethod]
    public void Tasks_create_at_the_top_and_queries_combine_search_and_tag_filter()
    {
        var document = new TaskBoardDocument();
        BoardTag house = AddTag(document, "House");
        BoardTag finance = AddTag(document, "Finance");
        DateTimeOffset now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
        TaskBoardOperations.CreateTask(document, "Old boiler invoice", "", "", null, [finance.Id], now.AddMinutes(-2));
        BoardTask newest = TaskBoardOperations.CreateTask(document, "Check heating offer", "Müller quotation", "", null, [house.Id, finance.Id], now);

        Assert.AreEqual(newest, document.Tasks[^1]);
        IReadOnlyList<BoardTask> result = TaskBoardQuery.VisibleTasks(
            document,
            BoardTaskStatus.ToDo,
            "müller",
            house.Id,
            includeArchived: false);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(newest.Id, result[0].Id);
    }

    [TestMethod]
    public void Assigning_a_tag_is_idempotent_and_updates_the_task_once()
    {
        var task = new BoardTask();
        Guid tagId = Guid.NewGuid();
        DateTimeOffset assigned = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(TaskBoardOperations.AssignTag(task, tagId, assigned));
        Assert.IsFalse(TaskBoardOperations.AssignTag(task, tagId, assigned.AddMinutes(1)));
        CollectionAssert.AreEqual(new[] { tagId }, task.TagIds.ToArray());
        Assert.AreEqual(assigned, task.UpdatedUtc);
    }

    [TestMethod]
    public void Lifecycle_moves_done_to_archive_and_trash_restores_previous_status()
    {
        var document = new TaskBoardDocument();
        DateTimeOffset start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        BoardTask task = TaskBoardOperations.CreateTask(document, "Finish report", "", "", null, [], start);
        TaskBoardOperations.Move(task, BoardTaskStatus.Done, start.AddMinutes(1));
        Assert.AreEqual(BoardTaskStatus.Done, task.Status);
        Assert.AreEqual(1, TaskBoardOperations.Housekeep(document, start.AddDays(7).AddMinutes(1)));
        Assert.IsNotNull(task.ArchivedUtc);
        TaskBoardOperations.RestoreArchive(task, start.AddDays(8));
        TaskBoardOperations.Delete(task, start.AddDays(9));
        Assert.IsTrue(task.DeletedUtc.HasValue);
        TaskBoardOperations.RestoreTrash(task, start.AddDays(10));
        Assert.AreEqual(BoardTaskStatus.Done, task.Status);
        Assert.IsNull(task.DeletedUtc);
        Assert.IsNull(task.ArchivedUtc);
        Assert.AreEqual(1, TaskBoardOperations.Housekeep(document, start.AddDays(38)));
        Assert.IsNotNull(task.ArchivedUtc);
    }

    [TestMethod]
    public void Empty_trash_removes_only_deleted_tasks()
    {
        var document = new TaskBoardDocument();
        DateTimeOffset now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        BoardTask keep = TaskBoardOperations.CreateTask(document, "Keep", "", "", null, [], now);
        BoardTask remove = TaskBoardOperations.CreateTask(document, "Remove", "", "", null, [], now);
        TaskBoardOperations.Delete(remove, now);

        Assert.AreEqual(1, TaskBoardOperations.EmptyTrash(document));
        Assert.AreSame(keep, document.Tasks.Single());
        Assert.AreEqual(0, TaskBoardOperations.EmptyTrash(document));
    }

    [TestMethod]
    public void Store_recovers_backup_and_blocks_writes_when_both_files_are_invalid()
    {
        InDirectory(path =>
        {
            var initial = new TaskBoardStore(path);
            TaskBoardDocument document = initial.Load();
            BoardTag tag = AddTag(document, "House");
            TaskBoardOperations.CreateTask(document, "Preserve me", "", "", null, [tag.Id], DateTimeOffset.UtcNow);
            initial.Save(document);
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, "{broken");

            var recovered = new TaskBoardStore(path);
            TaskBoardDocument loaded = recovered.Load();
            Assert.IsTrue(recovered.CanSave);
            Assert.IsNotNull(recovered.LoadWarning);
            Assert.AreEqual("Preserve me", loaded.Tasks.Single().Title);
            recovered.Save(loaded);
            Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length);
        });

        InDirectory(path =>
        {
            File.WriteAllText(path, "{broken");
            File.WriteAllText(path + ".bak", "{also broken");
            var store = new TaskBoardStore(path);
            _ = store.Load();
            Assert.IsFalse(store.CanSave);
            Assert.ThrowsExactly<IOException>(() => store.Save(new TaskBoardDocument()));
            Assert.AreEqual("{broken", File.ReadAllText(path));
        });
    }

    [TestMethod]
    public void Store_restores_recovery_copy_and_keeps_pending_description_edits()
    {
        InDirectory(path =>
        {
            var initial = new TaskBoardStore(path);
            TaskBoardDocument document = initial.Load();
            BoardTask task = TaskBoardOperations.CreateTask(document, "Keep recovery", "saved", "", null, [], DateTimeOffset.UtcNow);
            initial.Save(document);

            string pending = new string('x', 100_001);
            initial.SaveRecovery(document, new Dictionary<Guid, string> { [task.Id] = pending });

            var restored = new TaskBoardStore(path);
            TaskBoardDocument loaded = restored.Load();

            Assert.IsTrue(restored.CanSave);
            Assert.IsTrue(restored.IsRecovered);
            Assert.IsNotNull(restored.LoadWarning);
            Assert.AreEqual("saved", loaded.Tasks.Single().Description);
            Assert.AreEqual(pending, restored.RecoveryPendingDescriptions[task.Id]);
        });
    }

    [TestMethod]
    public void Store_recovery_keeps_an_over_limit_capture_draft()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            document.Draft = new TaskCaptureDraft
            {
                OriginalTranscript = "captured",
                SuggestedTitle = "Draft",
                Description = new string('x', 100_001)
            };

            store.SaveRecovery(document, new Dictionary<Guid, string>());
            var restored = new TaskBoardStore(path);
            TaskBoardDocument loaded = restored.Load();

            Assert.IsNotNull(loaded.Draft);
            Assert.AreEqual(100_001, loaded.Draft!.Description.Length);
        });
    }

    [TestMethod]
    public void Store_does_not_downgrade_a_future_primary_to_an_older_backup()
    {
        InDirectory(path =>
        {
            var initial = new TaskBoardStore(path);
            TaskBoardDocument document = initial.Load();
            TaskBoardOperations.CreateTask(document, "Future primary", "", "", null, [], DateTimeOffset.UtcNow);
            initial.Save(document);
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Version\": 1", "\"Version\": 2", StringComparison.Ordinal));

            var store = new TaskBoardStore(path);
            TaskBoardDocument loaded = store.Load();

            Assert.IsFalse(store.CanSave);
            Assert.IsFalse(store.IsRecovered);
            Assert.IsNotNull(store.LoadWarning);
            Assert.AreEqual(0, loaded.Tasks.Count);
            StringAssert.Contains(File.ReadAllText(path), "\"Version\": 2");
        });
    }

    [TestMethod]
    public void Store_keeps_a_newer_primary_when_recovery_is_stale()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            TaskBoardOperations.CreateTask(document, "First", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            store.SaveRecovery(document, new Dictionary<Guid, string>());

            TaskBoardOperations.CreateTask(document, "Newer primary", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);

            var restored = new TaskBoardStore(path);
            TaskBoardDocument loaded = restored.Load();
            Assert.AreEqual(2, loaded.Tasks.Count);
            Assert.IsTrue(restored.RecoveryNeedsChoice);
            Assert.IsFalse(restored.IsRecovered);
        });
    }

    [TestMethod]
    public void Store_rejects_a_malformed_recovery_document_when_no_primary_exists()
    {
        InDirectory(path =>
        {
            var initial = new TaskBoardStore(path);
            TaskBoardDocument document = initial.Load();
            TaskBoardOperations.CreateTask(document, "Keep primary", "", "", null, [], DateTimeOffset.UtcNow);
            initial.Save(document);
            File.Delete(path);
            File.WriteAllText(path + ".recovery", "{\"Version\":1,\"BaseRevision\":1,\"HasPendingChanges\":true,\"Document\":{},\"PendingTitles\":{},\"PendingDescriptions\":{}}");

            var restored = new TaskBoardStore(path);
            _ = restored.Load();

            Assert.IsFalse(restored.CanSave);
            Assert.IsNotNull(restored.LoadWarning);
            Assert.IsTrue(File.Exists(path + ".recovery"));
        });
    }

    [TestMethod]
    public void Store_does_not_overwrite_recovery_that_requires_a_user_decision()
    {
        InDirectory(path =>
        {
            var initial = new TaskBoardStore(path);
            TaskBoardDocument document = initial.Load();
            BoardTask task = TaskBoardOperations.CreateTask(document, "Current", "", "", null, [], DateTimeOffset.UtcNow);
            initial.Save(document);
            initial.SaveRecovery(document, new Dictionary<Guid, string> { [task.Id] = "old" });
            task.Title = "Newer";
            initial.Save(document);

            var restored = new TaskBoardStore(path);
            TaskBoardDocument current = restored.Load();
            Assert.IsTrue(restored.RecoveryNeedsChoice);
            Assert.ThrowsExactly<IOException>(() => restored.SaveRecovery(current, new Dictionary<Guid, string> { [task.Id] = "new" }));
        });
    }

    [TestMethod]
    public void Store_recovers_quick_entry_text_without_a_task_draft()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            TaskBoardOperations.CreateTask(document, "Existing", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            store.SaveRecovery(document, new Dictionary<Guid, string>(), pendingQuickEntry: "Keep this quick entry");

            var restored = new TaskBoardStore(path);
            _ = restored.Load();

            Assert.AreEqual("Keep this quick entry", restored.RecoveryPendingQuickEntry);
        });
    }

    [TestMethod]
    public void Store_ignores_an_empty_quick_entry_recovery_copy_when_the_document_is_unchanged()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            TaskBoardOperations.CreateTask(document, "Existing", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            store.SaveRecovery(document, new Dictionary<Guid, string>(), pendingQuickEntry: string.Empty);

            var restored = new TaskBoardStore(path);
            TaskBoardDocument loaded = restored.Load();

            Assert.AreEqual(1, loaded.Tasks.Count);
            Assert.IsNull(restored.LoadWarning);
            Assert.IsNull(restored.RecoveryPendingQuickEntry);
        });
    }

    [TestMethod]
    public void Store_recovers_a_failed_metadata_write_without_a_draft()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            BoardTask task = TaskBoardOperations.CreateTask(document, "Before", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            task.Title = "After";
            store.SaveRecovery(document, new Dictionary<Guid, string>());

            var restored = new TaskBoardStore(path);
            TaskBoardDocument loaded = restored.Load();
            Assert.IsTrue(restored.IsRecovered);
            Assert.AreEqual("After", loaded.Tasks.Single().Title);
        });
    }

    [TestMethod]
    public void Store_saves_valid_task_changes_while_retaining_an_invalid_draft_for_recovery()
    {
        InDirectory(path =>
        {
            var store = new TaskBoardStore(path);
            TaskBoardDocument document = store.Load();
            BoardTask task = TaskBoardOperations.CreateTask(document, "Before", "", "", null, [], DateTimeOffset.UtcNow);
            store.Save(document);
            task.Title = "After";
            document.Draft = new TaskCaptureDraft { SuggestedTitle = "Draft", Description = new string('x', 100_001) };
            store.Save(document);

            var persisted = new TaskBoardStore(path);
            TaskBoardDocument loaded = persisted.Load();
            Assert.AreEqual("After", loaded.Tasks.Single().Title);
            Assert.IsNull(loaded.Draft);
            store.SaveRecovery(document, new Dictionary<Guid, string>());
            var recovered = new TaskBoardStore(path);
            Assert.IsNotNull(recovered.Load().Draft);
        });
    }

    [TestMethod]
    public void Draft_parser_preserves_source_and_marks_ambiguous_dates()
    {
        var tags = new[] { new BoardTag { Id = Guid.NewGuid(), Name = "House", Color = "green" } };
        DateOnly today = new(2026, 9, 12);
        TaskDraftParseResult parsed = TaskDraftParser.Parse(
            "Den Wasserfilter nächste Woche bestellen, Tags House und Einkauf.", tags, today);

        Assert.AreEqual("Den Wasserfilter nächste Woche bestellen, Tags House und Einkauf.", parsed.OriginalTranscript);
        Assert.AreEqual(parsed.OriginalTranscript, parsed.Description);
        Assert.AreEqual("Den Wasserfilter bestellen", parsed.SuggestedTitle);
        Assert.IsNull(parsed.SuggestedDueDate);
        Assert.AreEqual("nächste Woche", parsed.UnresolvedDatePhrase);
        Assert.AreEqual(1, parsed.SelectedTagIds.Count);
        CollectionAssert.AreEqual(new[] { "Einkauf" }, parsed.ExplicitTagNames.ToArray());
    }

    [TestMethod]
    public void Draft_parser_does_not_guess_when_multiple_dates_are_dictated()
    {
        TaskDraftParseResult parsed = TaskDraftParser.Parse(
            "Invoice by 03.04 or tomorrow, Tags Haus, Einkauf und Finanzen",
            [],
            new DateOnly(2026, 9, 12));

        Assert.IsNull(parsed.SuggestedDueDate);
        StringAssert.Contains(parsed.UnresolvedDatePhrase, "03.04");
        StringAssert.Contains(parsed.UnresolvedDatePhrase, "tomorrow");
        Assert.AreEqual("Invoice by or", parsed.SuggestedTitle);
        CollectionAssert.AreEqual(new[] { "Haus", "Einkauf", "Finanzen" }, parsed.ExplicitTagNames.ToArray());
    }

    [TestMethod]
    public void Draft_parser_resolves_relative_and_yearless_dates_against_reference_day()
    {
        DateOnly reference = new(2026, 12, 30);
        Assert.AreEqual(new DateOnly(2027, 1, 2), TaskDraftParser.Parse("Send it in 3 days", [], reference).SuggestedDueDate);
        Assert.AreEqual(new DateOnly(2027, 1, 1), TaskDraftParser.Parse("Send it on 1.1", [], reference).SuggestedDueDate);
    }

    [TestMethod]
    public void Draft_parser_handles_two_digit_months_and_leap_days_with_reference_date()
    {
        Assert.AreEqual(new DateOnly(2028, 10, 1), TaskDraftParser.Parse("Send it on 1.10", [], new DateOnly(2027, 12, 30)).SuggestedDueDate);
        Assert.AreEqual(new DateOnly(2028, 2, 29), TaskDraftParser.Parse("Send it on 29.2", [], new DateOnly(2028, 1, 1)).SuggestedDueDate);
    }

    [TestMethod]
    public void Due_query_excludes_done_archived_and_trash()
    {
        var document = new TaskBoardDocument();
        DateTimeOffset now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
        BoardTask due = TaskBoardOperations.CreateTask(document, "Due", "", "", new DateOnly(2026, 9, 12), [], now);
        BoardTask done = TaskBoardOperations.CreateTask(document, "Done", "", "", new DateOnly(2026, 9, 12), [], now);
        TaskBoardOperations.Move(done, BoardTaskStatus.Done, now);
        BoardTask deleted = TaskBoardOperations.CreateTask(document, "Deleted", "", "", new DateOnly(2026, 9, 12), [], now);
        TaskBoardOperations.Delete(deleted, now);

        IReadOnlyList<BoardTask> result = TaskBoardQuery.DueOrOverdueOpenTasks(document, new DateOnly(2026, 9, 12));
        CollectionAssert.AreEqual(new[] { due.Id }, result.Select(task => task.Id).ToArray());
    }

    [TestMethod]
    public void Daily_summary_lists_at_most_three_open_due_tasks()
    {
        var document = new TaskBoardDocument();
        DateOnly today = new(2026, 9, 12);
        for (int index = 0; index < 4; index++)
            TaskBoardOperations.CreateTask(document, $"Task {index}", "", "", today, [], DateTimeOffset.UtcNow);

        TaskDailySummary summary = TaskDailySummary.Create(document, today);

        Assert.AreEqual(4, summary.Count);
        Assert.AreEqual(3, summary.Titles.Count);
        StringAssert.Contains(summary.NotificationBody, "+1 more");
    }

    [TestMethod]
    public void Daily_summary_bounds_notification_text_without_losing_count()
    {
        var document = new TaskBoardDocument();
        DateOnly today = new(2026, 9, 12);
        for (int index = 0; index < 4; index++)
            TaskBoardOperations.CreateTask(document, new string((char)('A' + index), 400), "", "", today, [], DateTimeOffset.UtcNow);

        TaskDailySummary summary = TaskDailySummary.Create(document, today);

        Assert.IsTrue(summary.NotificationBody.Length <= 220);
        StringAssert.Contains(summary.NotificationBody, "+1 more");
    }

    private static BoardTag AddTag(TaskBoardDocument document, string name)
    {
        var tag = new BoardTag { Name = name, Color = "blue" };
        document.Tags.Add(tag);
        return tag;
    }

    private static void InDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinWisprTaskTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "tasks.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
