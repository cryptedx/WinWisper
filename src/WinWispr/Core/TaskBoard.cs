using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinWispr.Core;

internal enum BoardTaskStatus
{
    ToDo,
    Doing,
    Done
}

internal enum BoardTaskSort
{
    Manual,
    TitleAscending,
    TitleDescending,
    DueAscending,
    DueDescending,
    CreatedDescending,
    CreatedAscending
}

internal sealed class BoardTask
{
    internal BoardTask Copy()
    {
        var copy = (BoardTask)MemberwiseClone();
        copy.TagIds = [.. TagIds];
        return copy;
    }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OriginalTranscript { get; set; } = string.Empty;
    public BoardTaskStatus Status { get; set; }
    public DateOnly? DueDate { get; set; }
    public List<Guid> TagIds { get; set; } = [];
    public long ManualOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public DateTimeOffset? ArchivedUtc { get; set; }
    public DateTimeOffset? DeletedUtc { get; set; }
    public BoardTaskStatus? DeletedFromStatus { get; set; }
    public bool DeletedFromArchive { get; set; }
}

internal sealed class BoardTag
{
    internal BoardTag Copy() => (BoardTag)MemberwiseClone();
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "blue";
}

internal sealed class TaskCaptureDraft
{
    internal TaskCaptureDraft Copy()
    {
        var copy = (TaskCaptureDraft)MemberwiseClone();
        copy.SelectedTagIds = [.. SelectedTagIds];
        copy.SuggestedTagIds = [.. SuggestedTagIds];
        copy.ExplicitTagNames = [.. ExplicitTagNames];
        return copy;
    }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalTranscript { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SuggestedTitle { get; set; } = string.Empty;
    public DateOnly? SuggestedDueDate { get; set; }
    public string? UnresolvedDatePhrase { get; set; }
    public List<Guid> SelectedTagIds { get; set; } = [];
    public List<Guid> SuggestedTagIds { get; set; } = [];
    public List<string> ExplicitTagNames { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class TaskBoardViewState
{
    internal TaskBoardViewState Copy() => (TaskBoardViewState)MemberwiseClone();
    public string Search { get; set; } = string.Empty;
    public Guid? TagFilter { get; set; }
    public BoardTaskSort Sort { get; set; } = BoardTaskSort.Manual;
    public bool ShowArchived { get; set; }
    public BoardTaskStatus NarrowStatus { get; set; } = BoardTaskStatus.ToDo;
    public int DrawerWidth { get; set; } = 400;
}

internal sealed class TaskBoardDocument
{
    internal TaskBoardDocument Copy()
    {
        var copy = (TaskBoardDocument)MemberwiseClone();
        copy.Tasks = Tasks.Select(task => task.Copy()).ToList();
        copy.Tags = Tags.Select(tag => tag.Copy()).ToList();
        copy.Draft = Draft?.Copy();
        copy.View = View.Copy();
        return copy;
    }
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<BoardTask> Tasks { get; set; } = [];
    public List<BoardTag> Tags { get; set; } = [];
    public TaskCaptureDraft? Draft { get; set; }
    public TaskBoardViewState View { get; set; } = new();
    public DateOnly? LastSummaryLocalDate { get; set; }
}

internal sealed class TaskBoardRecoveryFile
{
    public int Version { get; set; } = 1;
    public long BaseRevision { get; set; }
    public bool HasPendingChanges { get; set; }
    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
    public TaskBoardDocument Document { get; set; } = new();
    public TaskCaptureDraft? Draft { get; set; }
    public Dictionary<Guid, string> PendingTitles { get; set; } = [];
    public Dictionary<Guid, string> PendingDescriptions { get; set; } = [];
    public string? PendingQuickEntry { get; set; }
}

internal sealed class TaskBoardStore(string path)
{
    private const int CurrentVersion = 1;
    private const int RecoveryVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private bool _recovered;
    private bool _canSave;

    internal string Path => path;
    internal string RecoveryPath => path + ".recovery";
    internal bool CanSave => _canSave;
    internal bool IsRecovered => _recovered;
    internal bool RecoveryNeedsChoice { get; private set; }
    internal string? LoadWarning { get; private set; }
    internal IReadOnlyDictionary<Guid, string> RecoveryPendingDescriptions { get; private set; } = new Dictionary<Guid, string>();
    internal IReadOnlyDictionary<Guid, string> RecoveryPendingTitles { get; private set; } = new Dictionary<Guid, string>();
    internal string? RecoveryPendingQuickEntry { get; private set; }

    internal TaskBoardDocument Load()
    {
        _canSave = false;
        _recovered = false;
        LoadWarning = null;
        RecoveryNeedsChoice = false;
        RecoveryPendingDescriptions = new Dictionary<Guid, string>();
        RecoveryPendingTitles = new Dictionary<Guid, string>();
        RecoveryPendingQuickEntry = null;
        bool primaryMissing = !File.Exists(path);

        if (TryLoad(path, out TaskBoardDocument? document, out Exception? primaryError))
        {
            if (TryLoadRecovery(RecoveryPath, out TaskBoardRecoveryFile? primaryRecovery, out bool primaryRecoveryInvalid)
                && primaryRecovery is not null
                && HasPendingRecovery(primaryRecovery, document))
            {
                if (primaryRecovery.BaseRevision == document!.Revision)
                {
                    _canSave = true;
                    _recovered = true;
                    RecoveryPendingDescriptions = primaryRecovery.PendingDescriptions;
                    RecoveryPendingTitles = primaryRecovery.PendingTitles;
                    RecoveryPendingQuickEntry = primaryRecovery.PendingQuickEntry;
                    LoadWarning = "Tasks were recovered from an unfinished edit. Check the task board before editing.";
                    return AttachRecoveryDraft(primaryRecovery);
                }

                // Keep the recovery file for an explicit user decision, but never
                // replace a valid primary with an older snapshot.
                RecoveryNeedsChoice = true;
                LoadWarning = "A recovery copy from an older board revision is available. The current saved board was kept.";
            }
            else if (primaryRecoveryInvalid)
            {
                RecoveryNeedsChoice = true;
                LoadWarning = "A damaged recovery copy is available. The current saved board was kept.";
            }
            _canSave = true;
            return document!;
        }

        if (primaryError is JsonException { Message: var message }
            && message.StartsWith("Unsupported task data version", StringComparison.Ordinal))
        {
            _canSave = false;
            LoadWarning = "Tasks use a newer format. Your files are preserved and saving is disabled until WinWispr is updated.";
            return new TaskBoardDocument();
        }

        if (TryLoadRecovery(RecoveryPath, out TaskBoardRecoveryFile? fallbackRecovery, out bool recoveryInvalid)
            && fallbackRecovery is not null
            && HasPendingRecovery(fallbackRecovery))
        {
            _canSave = true;
            _recovered = true;
            RecoveryPendingDescriptions = fallbackRecovery.PendingDescriptions;
            RecoveryPendingTitles = fallbackRecovery.PendingTitles;
            RecoveryPendingQuickEntry = fallbackRecovery.PendingQuickEntry;
            LoadWarning = "Tasks were recovered from an unfinished edit. Check the task board before editing.";
            return AttachRecoveryDraft(fallbackRecovery);
        }

        if (recoveryInvalid)
        {
            _canSave = false;
            LoadWarning = "The task recovery copy is damaged. Your files are preserved and saving is disabled.";
            return new TaskBoardDocument();
        }

        if (TryLoad(path + ".bak", out document, out _))
        {
            _canSave = true;
            _recovered = true;
            LoadWarning = primaryMissing
                ? "Tasks were recovered from a backup."
                : "Tasks were recovered from a backup. Check the task board before editing.";
            return document!;
        }

        if (primaryMissing && !File.Exists(path + ".bak"))
        {
            _canSave = true;
            return new TaskBoardDocument();
        }

        _canSave = false;
        LoadWarning = primaryError is UnauthorizedAccessException
            ? "Tasks could not be read. Your files are preserved and saving is disabled."
            : "Tasks could not be read. Your files are preserved and saving is disabled. Restore tasks.json from a backup and restart WinWispr.";
        return new TaskBoardDocument();
    }

    internal void Save(TaskBoardDocument document)
    {
        if (!_canSave)
            throw new IOException("Task data is protected after a load failure. Restore tasks.json and restart WinWispr.");

        ArgumentNullException.ThrowIfNull(document);
        TaskBoardDocument snapshot = Clone(document);
        if (snapshot.Draft is not null && !IsValidDraft(snapshot.Draft, snapshot))
            snapshot.Draft = null;
        Validate(snapshot);
        long nextRevision = checked(document.Revision + 1);
        snapshot.Revision = nextRevision;
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (directory is null)
            throw new IOException("Task data path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                string backup = _recovered
                    ? path + ".corrupt-" + Guid.NewGuid().ToString("N")
                    : path + ".bak";
                File.Replace(temporary, path, backup);
            }
            else
            {
                File.Move(temporary, path);
            }

            _recovered = false;
            document.Revision = nextRevision;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal void SaveRecovery(TaskBoardDocument document, IReadOnlyDictionary<Guid, string> pendingDescriptions, IReadOnlyDictionary<Guid, string>? pendingTitles = null, string? pendingQuickEntry = null)
    {
        if (!_canSave)
            throw new IOException("Task data is protected after a load failure. Restore tasks.json and restart WinWispr.");
        if (RecoveryNeedsChoice)
            throw new IOException("An older recovery copy requires an explicit decision before it can be replaced.");

        TaskBoardDocument snapshot = Clone(document);
        TaskCaptureDraft? draft = snapshot.Draft;
        snapshot.Draft = null;
        Validate(snapshot);
        WriteRecovery(RecoveryPath, document, snapshot, draft, pendingDescriptions, pendingTitles, pendingQuickEntry);
    }

    internal void SaveRecoveryTo(string targetPath, TaskBoardDocument document, IReadOnlyDictionary<Guid, string> pendingDescriptions, IReadOnlyDictionary<Guid, string>? pendingTitles = null, string? pendingQuickEntry = null)
    {
        if (!_canSave)
            throw new IOException("Task data is protected after a load failure. Restore tasks.json and restart WinWispr.");
        TaskBoardDocument snapshot = Clone(document);
        TaskCaptureDraft? draft = snapshot.Draft;
        snapshot.Draft = null;
        Validate(snapshot);
        WriteRecovery(targetPath, document, snapshot, draft, pendingDescriptions, pendingTitles, pendingQuickEntry);
    }

    private static void WriteRecovery(
        string targetPath,
        TaskBoardDocument source,
        TaskBoardDocument snapshot,
        TaskCaptureDraft? draft,
        IReadOnlyDictionary<Guid, string> pendingDescriptions,
        IReadOnlyDictionary<Guid, string>? pendingTitles,
        string? pendingQuickEntry)
    {
        string? directory = System.IO.Path.GetDirectoryName(targetPath);
        if (directory is null)
            throw new IOException("Task recovery path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var recovery = new TaskBoardRecoveryFile
            {
                BaseRevision = source.Revision,
                HasPendingChanges = true,
                Document = snapshot,
                Draft = draft,
                PendingTitles = (pendingTitles ?? new Dictionary<Guid, string>()).ToDictionary(pair => pair.Key, pair => pair.Value),
                PendingDescriptions = pendingDescriptions.ToDictionary(pair => pair.Key, pair => pair.Value),
                PendingQuickEntry = pendingQuickEntry
            };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, recovery, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal void ClearRecovery()
    {
        File.Delete(RecoveryPath);
    }

    internal string? CreateRecoveryImportBackup()
    {
        if (!File.Exists(path) && !File.Exists(RecoveryPath))
            return null;
        string suffix = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        string backupPath = path + ".before-import-" + suffix;
        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: false);
        if (File.Exists(RecoveryPath))
            File.Copy(RecoveryPath, backupPath + ".recovery", overwrite: false);
        return backupPath;
    }

    internal byte[]? CaptureRecoveryCopy() => File.Exists(RecoveryPath) ? File.ReadAllBytes(RecoveryPath) : null;

    internal void RestoreRecoveryCopy(byte[]? contents)
    {
        if (contents is null)
        {
            if (File.Exists(RecoveryPath))
                File.Delete(RecoveryPath);
            return;
        }

        string? directory = System.IO.Path.GetDirectoryName(RecoveryPath);
        if (directory is null)
            throw new IOException("Task recovery path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = RecoveryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, RecoveryPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal void ResetRecoveryState()
    {
        _recovered = false;
        RecoveryNeedsChoice = false;
        LoadWarning = null;
        RecoveryPendingDescriptions = new Dictionary<Guid, string>();
        RecoveryPendingTitles = new Dictionary<Guid, string>();
        RecoveryPendingQuickEntry = null;
    }

    // Called by the board's single ordered worker, using an isolated snapshot.
    internal TaskBoardSaveResult SaveWithRecovery(TaskBoardDocument snapshot,
        Dictionary<Guid, string> descriptions, Dictionary<Guid, string> titles,
        string? quickEntry, bool allowRecoveryCleanup)
    {
        try
        {
            Save(snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            bool recovered = false;
            try { SaveRecovery(snapshot, descriptions, titles, quickEntry); recovered = true; }
            catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or JsonException) { }
            return new(snapshot, false, recovered, error.Message);
        }

        var tasks = snapshot.Tasks.ToDictionary(task => task.Id);
        foreach (Guid id in descriptions.Keys.ToArray())
            if (tasks.TryGetValue(id, out BoardTask? task) && task.Description == descriptions[id])
                descriptions.Remove(id);
        foreach (Guid id in titles.Keys.ToArray())
            if (tasks.TryGetValue(id, out BoardTask? task) && task.Title == titles[id])
                titles.Remove(id);
        try
        {
            if (!RecoveryNeedsChoice)
            {
                if (snapshot.Draft is not null || descriptions.Count > 0 || titles.Count > 0 || quickEntry is not null)
                    SaveRecovery(snapshot, descriptions, titles, quickEntry);
                else if (allowRecoveryCleanup)
                    ClearRecovery();
            }
            return new(snapshot, true, false, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(snapshot, true, false, error.Message);
        }
    }

    internal void AcknowledgeRecoveryChoice() => RecoveryNeedsChoice = false;

    internal void AcknowledgeRecovery() => _recovered = false;

    internal static string RecoveryContentIdentity(TaskBoardDocument document,
        IReadOnlyDictionary<Guid, string> descriptions, IReadOnlyDictionary<Guid, string> titles, string? quickEntry) =>
        JsonSerializer.Serialize(new
        {
            document.Version, document.Tasks, document.Tags, document.Draft, document.View,
            document.LastSummaryLocalDate, Descriptions = descriptions, Titles = titles, QuickEntry = quickEntry
        }, JsonOptions);

    internal void RefreshBackup()
    {
        if (!_canSave || !File.Exists(path))
            return;
        File.Copy(path, path + ".bak", overwrite: true);
    }

    internal static void Validate(TaskBoardDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != CurrentVersion)
            throw new JsonException($"Unsupported task data version {document.Version}.");
        if (document.Revision < 0)
            throw new JsonException("Task data revision is invalid.");
        if (document.Tasks is null || document.Tags is null || document.View is null)
            throw new JsonException("Task data is incomplete.");
        if (document.LastSummaryLocalDate is DateOnly summaryDate && summaryDate.Year is < 1900 or > 9999)
            throw new JsonException("Summary date is invalid.");
        if (!Enum.IsDefined(document.View.Sort) || !Enum.IsDefined(document.View.NarrowStatus)
            || document.View.DrawerWidth is < 300 or > 700)
            throw new JsonException("Task board view state is invalid.");

        var taskIds = new HashSet<Guid>();
        foreach (BoardTask task in document.Tasks)
        {
            if (task is null || task.Id == Guid.Empty || !taskIds.Add(task.Id))
                throw new JsonException("Task identifiers must be unique.");
            if (string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 400
                || task.Description is null || task.Description.Length > 100_000
                || task.OriginalTranscript is null || task.OriginalTranscript.Length > 100_000
                || !Enum.IsDefined(task.Status) || task.TagIds is null)
                throw new JsonException("Task data is invalid.");
            if (task.DueDate is DateOnly due && due.Year is < 1900 or > 9999)
                throw new JsonException("Task due date is invalid.");
        }

        var tagIds = new HashSet<Guid>();
        foreach (BoardTag tag in document.Tags)
        {
            if (tag is null || tag.Id == Guid.Empty || !tagIds.Add(tag.Id)
                || string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 80
                || string.IsNullOrWhiteSpace(tag.Color) || tag.Color.Length > 32)
                throw new JsonException("Tag data is invalid.");
        }

        foreach (BoardTask task in document.Tasks)
        {
            if (task.TagIds.Any(id => !tagIds.Contains(id)))
                throw new JsonException("Task references an unknown tag.");
        }

        if (document.Draft is not null)
        {
            if (document.Draft.OriginalTranscript is null || document.Draft.OriginalTranscript.Length > 100_000
                || document.Draft.Description is null || document.Draft.Description.Length > 100_000
                || document.Draft.SuggestedTitle is null || document.Draft.SuggestedTitle.Length > 400
                || document.Draft.SelectedTagIds is null || document.Draft.SuggestedTagIds is null || document.Draft.ExplicitTagNames is null
                || document.Draft.SelectedTagIds.Any(id => !tagIds.Contains(id))
                || document.Draft.SuggestedTagIds.Any(id => !tagIds.Contains(id)))
                throw new JsonException("Task draft is invalid.");
        }
    }

    private static bool TryLoad(string candidate, out TaskBoardDocument? document, out Exception? error)
    {
        document = null;
        error = null;
        try
        {
            if (!File.Exists(candidate))
                throw new FileNotFoundException("Task data file was not found.", candidate);
            string json = File.ReadAllText(candidate);
            using JsonDocument envelope = JsonDocument.Parse(json);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object
                || !envelope.RootElement.TryGetProperty("Version", out _)
                || !envelope.RootElement.TryGetProperty("Tasks", out _)
                || !envelope.RootElement.TryGetProperty("Tags", out _)
                || !envelope.RootElement.TryGetProperty("View", out _))
                throw new JsonException("Task data envelope is incomplete.");
            document = JsonSerializer.Deserialize<TaskBoardDocument>(json, JsonOptions)
                ?? throw new JsonException("Task data is empty.");
            Validate(document);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = exception;
            return false;
        }
    }

    internal static bool TryLoadRecovery(string candidate, out TaskBoardRecoveryFile? recovery, out bool invalid)
    {
        recovery = null;
        invalid = false;
        try
        {
            if (!File.Exists(candidate))
                return false;
            string json = File.ReadAllText(candidate);
            using JsonDocument envelope = JsonDocument.Parse(json);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object
                || !envelope.RootElement.TryGetProperty("Version", out _)
                || !envelope.RootElement.TryGetProperty("Document", out _)
                || !envelope.RootElement.TryGetProperty("BaseRevision", out _)
                || !envelope.RootElement.TryGetProperty("HasPendingChanges", out _)
                || !envelope.RootElement.TryGetProperty("PendingTitles", out _)
                || !envelope.RootElement.TryGetProperty("PendingDescriptions", out _))
                throw new JsonException("Task recovery envelope is incomplete.");
            JsonElement nestedDocument = envelope.RootElement.GetProperty("Document");
            if (nestedDocument.ValueKind != JsonValueKind.Object
                || !nestedDocument.TryGetProperty("Version", out _)
                || !nestedDocument.TryGetProperty("Revision", out _)
                || !nestedDocument.TryGetProperty("Tasks", out _)
                || !nestedDocument.TryGetProperty("Tags", out _)
                || !nestedDocument.TryGetProperty("View", out _))
                throw new JsonException("Task recovery document envelope is incomplete.");
            recovery = JsonSerializer.Deserialize<TaskBoardRecoveryFile>(json, JsonOptions)
                ?? throw new JsonException("Task recovery is empty.");
            if (recovery.Version != RecoveryVersion || recovery.Document is null || recovery.PendingDescriptions is null
                || recovery.BaseRevision < 0)
                throw new JsonException("Task recovery is invalid.");
            if (recovery.PendingTitles is null
                || recovery.PendingTitles.Any(pair => pair.Value is null)
                || recovery.PendingDescriptions.Any(pair => pair.Value is null))
                throw new JsonException("Task recovery contains an invalid edit.");
            Validate(recovery.Document);
            HashSet<Guid> taskIds = recovery.Document.Tasks.Select(task => task.Id).ToHashSet();
            if (recovery.PendingTitles.Keys.Any(id => !taskIds.Contains(id))
                || recovery.PendingDescriptions.Keys.Any(id => !taskIds.Contains(id)))
                throw new JsonException("Task recovery references an unknown task.");
            if (recovery.Draft is not null)
                ValidateRecoveryDraft(recovery.Draft, recovery.Document);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            invalid = File.Exists(candidate);
            return false;
        }
    }

    private static bool HasPendingRecovery(TaskBoardRecoveryFile recovery, TaskBoardDocument? primary = null)
    {
        if (recovery.Draft is not null || recovery.Document.Draft is not null
            || recovery.PendingTitles.Count > 0 || recovery.PendingDescriptions.Count > 0
            || !string.IsNullOrWhiteSpace(recovery.PendingQuickEntry))
            return true;
        if (!recovery.HasPendingChanges)
            return false;
        return primary is null || JsonSerializer.Serialize(recovery.Document, JsonOptions)
            != JsonSerializer.Serialize(primary, JsonOptions);
    }

    private static TaskBoardDocument AttachRecoveryDraft(TaskBoardRecoveryFile recovery)
    {
        if (recovery.Draft is not null)
            recovery.Document.Draft = recovery.Draft;
        return recovery.Document;
    }

    private static void ValidateRecoveryDraft(TaskCaptureDraft draft, TaskBoardDocument document)
    {
        if (draft.Id == Guid.Empty
            || draft.OriginalTranscript is null
            || draft.Description is null
            || draft.SuggestedTitle is null
            || draft.SelectedTagIds is null
            || draft.SuggestedTagIds is null
            || draft.ExplicitTagNames is null
            || draft.SelectedTagIds.Any(id => !document.Tags.Any(tag => tag.Id == id))
            || draft.SuggestedTagIds.Any(id => !document.Tags.Any(tag => tag.Id == id)))
            throw new JsonException("Task recovery draft is invalid.");
    }

    private static bool IsValidDraft(TaskCaptureDraft draft, TaskBoardDocument document)
    {
        try
        {
            ValidateRecoveryDraft(draft, document);
            return draft.OriginalTranscript.Length <= 100_000
                && draft.Description.Length <= 100_000
                && draft.SuggestedTitle.Length <= 400;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TaskBoardDocument Clone(TaskBoardDocument document) =>
        JsonSerializer.Deserialize<TaskBoardDocument>(JsonSerializer.Serialize(document, JsonOptions), JsonOptions)
        ?? throw new JsonException("Task data snapshot is empty.");

}

internal sealed record TaskBoardSaveResult(TaskBoardDocument Snapshot, bool PrimarySaved, bool RecoverySaved, string? Error);

internal static class TaskBoardOperations
{
    internal static BoardTask CreateTask(
        TaskBoardDocument document,
        string title,
        string description,
        string originalTranscript,
        DateOnly? dueDate,
        IEnumerable<Guid> tagIds,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        List<BoardTask> openTasks = document.Tasks.Where(task => !IsDeleted(task) && !task.ArchivedUtc.HasValue).ToList();
        long order = openTasks.Where(task => task.Status == BoardTaskStatus.ToDo)
            .Select(task => task.ManualOrder)
            .DefaultIfEmpty(0)
            .Min() - 1;
        var task = new BoardTask
        {
            Title = title.Trim(),
            Description = description ?? string.Empty,
            OriginalTranscript = originalTranscript ?? string.Empty,
            Status = BoardTaskStatus.ToDo,
            DueDate = dueDate,
            TagIds = tagIds.Distinct().ToList(),
            ManualOrder = order,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        document.Tasks.Add(task);
        return task;
    }

    internal static bool AssignTag(BoardTask task, Guid tagId, DateTimeOffset now)
    {
        if (task.TagIds.Contains(tagId))
            return false;
        task.TagIds.Add(tagId);
        task.UpdatedUtc = now;
        return true;
    }

    internal static void Move(BoardTask task, BoardTaskStatus status, DateTimeOffset now)
    {
        if (task.Status == status && !task.ArchivedUtc.HasValue)
            return;
        task.Status = status;
        task.UpdatedUtc = now;
        task.ArchivedUtc = null;
        task.DeletedUtc = null;
        task.DeletedFromStatus = null;
        task.DeletedFromArchive = false;
        task.CompletedUtc = status == BoardTaskStatus.Done ? now : null;
    }

    internal static void Archive(BoardTask task, DateTimeOffset now)
    {
        if (task.Status != BoardTaskStatus.Done || task.DeletedUtc.HasValue)
            return;
        task.ArchivedUtc = now;
        task.UpdatedUtc = now;
    }

    internal static void RestoreArchive(BoardTask task, DateTimeOffset now)
    {
        if (!task.ArchivedUtc.HasValue)
            return;
        task.ArchivedUtc = null;
        task.CompletedUtc = now;
        task.UpdatedUtc = now;
    }

    internal static void Delete(BoardTask task, DateTimeOffset now)
    {
        if (task.DeletedUtc.HasValue)
            return;
        task.DeletedFromStatus = task.Status;
        task.DeletedFromArchive = task.ArchivedUtc.HasValue;
        task.DeletedUtc = now;
        task.ArchivedUtc = null;
        task.UpdatedUtc = now;
    }

    internal static void RestoreTrash(BoardTask task, DateTimeOffset now)
    {
        if (!task.DeletedUtc.HasValue)
            return;
        task.Status = task.DeletedFromStatus ?? BoardTaskStatus.ToDo;
        task.ArchivedUtc = null;
        task.CompletedUtc = task.Status == BoardTaskStatus.Done ? now : null;
        task.DeletedUtc = null;
        task.DeletedFromStatus = null;
        task.DeletedFromArchive = false;
        task.UpdatedUtc = now;
    }

    internal static int EmptyTrash(TaskBoardDocument document) =>
        document.Tasks.RemoveAll(task => task.DeletedUtc.HasValue);

    internal static int Housekeep(TaskBoardDocument document, DateTimeOffset now)
    {
        int changed = 0;
        foreach (BoardTask task in document.Tasks)
        {
            if (task.DeletedUtc is DateTimeOffset deleted && now - deleted >= TimeSpan.FromDays(30))
            {
                changed++;
                continue;
            }

            if (task.Status == BoardTaskStatus.Done && !task.ArchivedUtc.HasValue
                && task.CompletedUtc is DateTimeOffset completed
                && now - completed >= TimeSpan.FromDays(7))
            {
                task.ArchivedUtc = completed.AddDays(7);
                task.UpdatedUtc = now;
                changed++;
            }
        }

        document.Tasks.RemoveAll(task => task.DeletedUtc is DateTimeOffset deleted && now - deleted >= TimeSpan.FromDays(30));
        return changed;
    }

    internal static bool IsDeleted(BoardTask task) => task.DeletedUtc.HasValue;
}
