using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinWispr.Core;
using WinRT.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace WinWispr.Views;

internal sealed partial class TaskBoardWindow : Window
{
    private const int DefaultWidth = 1100;
    private const int DefaultHeight = 760;
    private const int MinimumWidth = 620;
    private const int MinimumHeight = 520;
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinTrackSizeWidthOffset = 24;
    private const int MinTrackSizeHeightOffset = 28;
    private readonly TaskBoardStore _store;
    private readonly TaskBoardDocument _document;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _retentionTimer;
    private readonly WindowProc _windowProc;
    private readonly Dictionary<BoardTaskStatus, ListView> _lists = [];
    private readonly Dictionary<BoardTaskStatus, Border> _columnSurfaces = [];
    private Border _quickEntryHost = null!;
    private readonly Dictionary<BoardTaskStatus, TextBlock> _counts = [];
    private WindowPlacement? _savedPlacement;
    private nint _windowHandle;
    private nint _originalWindowProc;
    private bool _placementApplied;
    private bool _minimumSizeHookInstalled;
    private bool _narrow;
    private bool _showDueOnly;
    private string? _savedSearch;
    private Guid? _savedTagFilter;
    private BoardTaskSort _savedSort;
    private BoardTaskStatus _activeNarrowStatus = BoardTaskStatus.ToDo;
    private Guid? _draggingTask;
    private Guid? _draggingTag;
    private bool _resizingDrawer;
    private double _drawerResizeStartX;
    private int _drawerResizeStartWidth;
    private Guid? _selectedTask;
    private bool _editingDescription;
    private bool _suppressEvents;
    private TextBox? _descriptionEditor;
    private readonly Dictionary<Guid, string> _descriptionEdits = [];
    private readonly Dictionary<Guid, string> _titleEdits = [];
    private Grid _boardGrid = null!;
    private Grid _content = null!;
    private Grid _toolbar = null!;
    private StackPanel _toolbarActions = null!;
    private StackPanel _createActions = null!;
    private StackPanel _tagStrip = null!;
    private StackPanel _narrowTabs = null!;
    private TextBox _searchBox = null!;
    private ComboBox _sortBox = null!;
    private CheckBox _archiveCheck = null!;
    private TextBlock? _saveStatus;
    private TextBlock? _footerSaveStatus;
    private bool _lastSaveFailed;
    private bool _writeFailed;
    private string _lastSaveText = "Saved";
    private bool _saveScheduled;
    private bool _recoveryAcknowledged;
    private Button? _retrySaveButton;
    private Button? _showAllDueButton;
    private string? _exportedRecoveryIdentity;
    private bool _quickEntryActive;
    private string _quickEntryText = string.Empty;
    private Border _drawer = null!;
    private Border _drawerResizeGrip = null!;
    private Grid _scrim = null!;
    private Grid _taskSavedHost = null!;
    private InfoBar _taskSavedBar = null!;
    private Button _taskSavedUndo = null!;
    private Guid? _lastCapturedTask;
    private int _drawerWidth;
    private RecordingMode _taskButtonMode;
    private bool _taskButtonPressed;
    private bool _taskButtonGestureWasHold;
    private TaskBoardViewState View => _document.View;

    internal TaskBoardWindow(
        TaskBoardStore store,
        TaskBoardDocument document,
        WindowPlacement? savedPlacement,
        int drawerWidth,
        DispatcherQueue dispatcher,
        RecordingMode taskButtonMode = RecordingMode.Toggle)
    {
        _store = store;
        _document = document;
        _savedPlacement = savedPlacement;
        _drawerWidth = Math.Clamp(drawerWidth, 300, 700);
        _taskButtonMode = taskButtonMode;
        _activeNarrowStatus = View.NarrowStatus;
        _dispatcher = dispatcher;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = SaveScheduledAsync();
        };
        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(120);
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshBoard(); };
        _retentionTimer = dispatcher.CreateTimer();
        _retentionTimer.Interval = TimeSpan.FromHours(1);
        _retentionTimer.Tick += (_, _) => HousekeepIfSafe();
        _retentionTimer.Start();
        _windowProc = WindowProcedure;
        InitializeComponent();
        foreach ((Guid taskId, string description) in _store.RecoveryPendingDescriptions)
        {
            _descriptionEdits[taskId] = description;
            if (_document.Tasks.FirstOrDefault(task => task.Id == taskId) is BoardTask task)
                task.Description = description;
        }
        foreach ((Guid taskId, string title) in _store.RecoveryPendingTitles)
            _titleEdits[taskId] = title;
        if (_store.RecoveryPendingQuickEntry is string quickEntry && !string.IsNullOrWhiteSpace(quickEntry))
        {
            _quickEntryActive = true;
            _quickEntryText = quickEntry;
        }
        BuildUi();

        Activated += OnActivated;
        Root.SizeChanged += (_, _) => UpdateResponsiveLayout();
        Root.KeyDown += OnRootKeyDown;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { _retentionTimer.Stop(); _refreshTimer.Stop(); RestoreMinimumSizeHook(); };

        _recoveryAcknowledged = _store.LoadWarning is null;
        HousekeepIfSafe();
        if (_store.LoadWarning is string warning)
            SetSaveStatus(warning, failed: true);
    }

    internal event Action? TaskCaptureRequested;
    internal event Action? TaskDraftResumeRequested;
    internal event Action? TaskCapturePressed;
    internal event Action? TaskCaptureReleased;
    internal event Action<WindowPlacement>? PlacementChanged;
    internal event Action<int>? DrawerWidthChanged;

    internal void SetTaskButtonMode(RecordingMode mode) => _taskButtonMode = mode;

    private bool BeginTaskButtonHold()
    {
        _taskButtonGestureWasHold = _taskButtonMode == RecordingMode.Hold;
        if (!_taskButtonGestureWasHold || _taskButtonPressed)
            return false;
        _taskButtonPressed = true;
        TaskCapturePressed?.Invoke();
        return true;
    }

    private void EndTaskButtonHold()
    {
        if (!_taskButtonPressed)
            return;
        _taskButtonPressed = false;
        TaskCaptureReleased?.Invoke();
    }

    internal bool IsVisible => Root.Visibility == Visibility.Visible;
    internal bool HasTaskDraft => _document.Draft is not null;
    internal TaskCaptureDraft? GetTaskDraft() => _document.Draft;
    internal bool HasPendingDurableData => _descriptionEdits.Count > 0 || _titleEdits.Count > 0
        || (_quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText)) || _writeFailed || _saveScheduled;

    internal bool TryPrepareForShutdown()
    {
        FlushPendingSave();
        return SaveRecoveryCopy() || (_exportedRecoveryIdentity is not null
            && _exportedRecoveryIdentity == CurrentRecoveryIdentity());
    }

    private string CurrentRecoveryIdentity() => TaskBoardStore.RecoveryContentIdentity(
        _document, _descriptionEdits, _titleEdits,
        _quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText) ? _quickEntryText : null);

    internal bool TrySetSummaryDate(DateOnly? date)
    {
        DateOnly? previous = _document.LastSummaryLocalDate;
        _document.LastSummaryLocalDate = date;
        if (TrySave())
            return true;
        _document.LastSummaryLocalDate = previous;
        TryWriteRecoveryCopy();
        return false;
    }

    internal bool DiscardTaskDraft(Guid draftId)
    {
        if (_document.Draft?.Id != draftId)
            return false;
        TaskCaptureDraft draft = _document.Draft;
        _document.Draft = null;
        if (TrySave())
            return true;
        _document.Draft = draft;
        TryWriteRecoveryCopy();
        return false;
    }

    internal bool DiscardStaleRecoveryCopy()
    {
        DrainWrite();
        if (!_store.RecoveryNeedsChoice)
            return false;
        try
        {
            _store.ClearRecovery();
            _store.AcknowledgeRecoveryChoice();
            _recoveryAcknowledged = true;
            SetSaveStatus("Current board kept; old recovery discarded", failed: false);
            HousekeepIfSafe();
            return true;
        }
        catch (IOException)
        {
            SetSaveStatus("Could not discard old recovery · retry", failed: true);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            SetSaveStatus("Could not discard old recovery · check access", failed: true);
            return false;
        }
    }

    internal bool AcknowledgeRecoveredData()
    {
        DrainWrite();
        if (_recoveryAcknowledged || _store.LoadWarning is null || _store.RecoveryNeedsChoice)
            return false;
        _recoveryAcknowledged = true;
        _store.AcknowledgeRecovery();
        SetSaveStatus("Recovered task data acknowledged", failed: false);
        HousekeepIfSafe();
        return true;
    }

    internal bool SaveRecoveryCopy()
    {
        DrainWrite();
        bool hasPendingData = HasPendingDurableData;
        if (_store.RecoveryNeedsChoice)
            return false;
        if (!_store.CanSave)
            return !hasPendingData;
        try
        {
            if (hasPendingData)
                _store.SaveRecovery(_document, _descriptionEdits, _titleEdits,
                    _quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText) ? _quickEntryText : null);
            else if (!_store.RecoveryNeedsChoice && (_recoveryAcknowledged || _store.LoadWarning is null))
                _store.ClearRecovery();
            else
                return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"WinWispr task recovery save failed: {exception.GetType().Name}");
            return false;
        }
    }

    internal void ActivateBoard()
    {
        Activate();
        AppWindow.Show();
        Root.Visibility = Visibility.Visible;
    }

    internal void ToggleBoard()
    {
        if (!IsVisible)
        {
            RestoreNormalView();
            ActivateBoard();
            return;
        }

        HideBoard();
    }

    private void HideBoard()
    {
        WindowPlacement placement = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        AppWindow.Hide();
        Root.Visibility = Visibility.Collapsed;
        FlushPendingSave();
        PlacementChanged?.Invoke(placement);
        DrawerWidthChanged?.Invoke(_drawerWidth);
    }

    /// <summary>Called by App when saving the window position or drawer width failed.</summary>
    internal void ShowWindowPositionSaveFailed() => SetSaveStatus("Could not save window position", failed: true);

    internal void ShowDueTasks()
    {
        if (_showDueOnly)
            return;
        _showDueOnly = true;
        _savedSearch = View.Search;
        _savedTagFilter = View.TagFilter;
        _savedSort = View.Sort;
        _suppressEvents = true;
        _searchBox.Text = string.Empty;
        _sortBox.SelectedIndex = SortIndex(BoardTaskSort.DueAscending);
        if (_showAllDueButton is not null)
            _showAllDueButton.Visibility = Visibility.Visible;
        _suppressEvents = false;
        ActivateBoard();
        RefreshBoard();
    }

    internal void RestoreNormalView()
    {
        if (!_showDueOnly)
            return;
        _showDueOnly = false;
        View.Search = _savedSearch ?? string.Empty;
        View.TagFilter = _savedTagFilter;
        View.Sort = _savedSort;
        TrySave();
        _suppressEvents = true;
        _searchBox.Text = View.Search;
        _sortBox.SelectedIndex = SortIndex(View.Sort);
        _suppressEvents = false;
        if (_showAllDueButton is not null)
            _showAllDueButton.Visibility = Visibility.Collapsed;
        RefreshBoard();
    }

    internal void OpenTask(Guid id)
    {
        if (_document.Tasks.Any(task => task.Id == id && !task.DeletedUtc.HasValue))
        {
            ActivateBoard();
            OpenDrawer(id);
        }
    }

    internal TaskCaptureDraft BeginTaskCapture(TaskDraftParseResult parsed)
    {
        if (_document.Draft is TaskCaptureDraft existing)
            return existing;
        var draft = new TaskCaptureDraft
        {
            OriginalTranscript = parsed.OriginalTranscript,
            Description = parsed.Description,
            SuggestedTitle = parsed.SuggestedTitle,
            SuggestedDueDate = parsed.SuggestedDueDate,
            UnresolvedDatePhrase = parsed.UnresolvedDatePhrase,
            SelectedTagIds = parsed.SelectedTagIds.Distinct().ToList(),
            SuggestedTagIds = parsed.SuggestedTagIds.Distinct().ToList(),
            ExplicitTagNames = parsed.ExplicitTagNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        _document.Draft = draft;
        TrySave();
        return draft;
    }

    internal void UpdateTaskCaptureDraft(
        Guid draftId,
        string title,
        string description,
        DateOnly? dueDate,
        IReadOnlyList<string> tagNames)
    {
        if (_document.Draft is not TaskCaptureDraft draft || draft.Id != draftId)
            return;
        draft.SuggestedTitle = title;
        draft.Description = description;
        draft.SuggestedDueDate = dueDate;
        draft.SelectedTagIds = tagNames
            .Select(name => _document.Tags.FirstOrDefault(tag => string.Equals(TaskDraftParser.Normalize(tag.Name), TaskDraftParser.Normalize(name), StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        draft.ExplicitTagNames = tagNames
            .Select(TaskDraftParser.Normalize)
            .Where(name => name.Length > 0 && !_document.Tags.Any(tag => string.Equals(TaskDraftParser.Normalize(tag.Name), name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        draft.UpdatedUtc = DateTimeOffset.UtcNow;
        ScheduleSave();
    }

    internal IReadOnlyList<BoardTag> GetTags() => _document.Tags.ToArray();

    internal BoardTask? CommitTaskCapture(
        Guid draftId,
        TaskDraftParseResult parsed,
        string title,
        string description,
        DateOnly? dueDate,
        IEnumerable<string> tagNames)
    {
        if (_document.Draft is not TaskCaptureDraft draft || draft.Id != draftId)
            return null;
        description ??= string.Empty;
        title = title.Trim();
        if (title.Length == 0 || title.Length > 400 || description.Length > 100_000)
        {
            SetTaskCaptureStatus(title.Length == 0 ? "Task title is required" : title.Length > 400 ? "Task title is too long" : "Description is too long");
            return null;
        }

        string[] normalizedTagNames = tagNames.Select(TaskDraftParser.Normalize).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalizedTagNames.Any(name => name.Length > 80))
        {
            SetTaskCaptureStatus("Tag names are limited to 80 characters");
            return null;
        }
        List<Guid> tagIds = new();
        List<Guid> createdTagIds = new();
        foreach (string name in normalizedTagNames)
        {
            BoardTag? tag = _document.Tags.FirstOrDefault(item =>
                string.Equals(TaskDraftParser.Normalize(item.Name), name, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                tag = new BoardTag { Name = name, Color = ColorForIndex(_document.Tags.Count) };
                _document.Tags.Add(tag);
                createdTagIds.Add(tag.Id);
            }
            tagIds.Add(tag.Id);
        }

        BoardTask task = TaskBoardOperations.CreateTask(
            _document,
            string.IsNullOrWhiteSpace(title) ? parsed.SuggestedTitle : title,
            description,
            parsed.OriginalTranscript,
            dueDate,
            tagIds,
            DateTimeOffset.UtcNow);
        _document.Draft = null;
        if (!PersistTask())
        {
            _document.Tasks.Remove(task);
            _document.Tags.RemoveAll(tag => createdTagIds.Contains(tag.Id));
            draft.Id = draftId;
            draft.OriginalTranscript = parsed.OriginalTranscript;
            draft.Description = description;
            draft.SuggestedTitle = title;
            draft.SuggestedDueDate = dueDate;
            draft.SelectedTagIds = tagIds.Where(id => _document.Tags.Any(tag => tag.Id == id)).ToList();
            draft.SuggestedTagIds = draft.SuggestedTagIds.Where(id => _document.Tags.Any(tag => tag.Id == id)).ToList();
            draft.ExplicitTagNames = tagNames.Select(TaskDraftParser.Normalize)
                .Where(name => name.Length > 0 && !_document.Tags.Any(tag => string.Equals(TaskDraftParser.Normalize(tag.Name), name, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            draft.UpdatedUtc = DateTimeOffset.UtcNow;
            _document.Draft = draft;
            TryWriteRecoveryCopy();
            return null;
        }
        ActivateBoard();
        RefreshBoard();
        OpenDrawer(task.Id);
        return task;
    }

    internal void SetTaskCaptureStatus(string status)
    {
        SetSaveStatus(status, failed: true);
    }

    internal void ShowTaskSaved(BoardTask task)
    {
        _lastCapturedTask = task.Id;
        _taskSavedHost.Visibility = Visibility.Visible;
        _taskSavedBar.Title = "Task saved";
        _taskSavedBar.Message = $"{task.Title} was added to To Do.";
        Button open = new() { Content = "Open task", Padding = new Thickness(8, 3, 8, 3) };
        AutomationProperties.SetName(open, "Open saved task");
        AutomationProperties.SetAutomationId(open, "open-saved-task");
        open.Click += (_, _) =>
        {
            HideTaskSavedNotice();
            OpenTask(task.Id);
        };
        _taskSavedBar.ActionButton = open;
        _taskSavedUndo.Visibility = Visibility.Visible;
        _taskSavedBar.IsOpen = true;
    }

    private void UndoCapturedTask(object sender, RoutedEventArgs args)
    {
        if (_lastCapturedTask is Guid id && _document.Tasks.FirstOrDefault(item => item.Id == id) is BoardTask current)
        {
            TaskBoardOperations.Delete(current, DateTimeOffset.UtcNow);
            PersistTask();
            RefreshBoard();
        }
        HideTaskSavedNotice();
    }

    private void HideTaskSavedNotice()
    {
        _taskSavedBar.IsOpen = false;
        _taskSavedUndo.Visibility = Visibility.Collapsed;
        _taskSavedHost.Visibility = Visibility.Collapsed;
    }

    private void BuildUi()
    {
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();

        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid titleBar = new() { Padding = new Thickness(16, 0, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
        SetTitleBar(titleBar);
        Grid.SetRow(titleBar, 0);
        var icon = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/WinWisprIcon.png")), Width = 21, Height = 21, Stretch = Stretch.Uniform };
        titleBar.Children.Add(icon);
        TextBlock title = new() { Text = "WinWispr", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Grid.SetColumn(title, 1);
        titleBar.Children.Add(title);
        Root.Children.Add(titleBar);

        _content = new Grid();
        _content.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ContentPointerPressed), true);
        Grid.SetRow(_content, 1);
        Root.Children.Add(_content);
        var rows = _content.RowDefinitions;
        rows.Add(new RowDefinition { Height = GridLength.Auto });
        rows.Add(new RowDefinition { Height = GridLength.Auto });
        rows.Add(new RowDefinition { Height = GridLength.Auto });
        rows.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rows.Add(new RowDefinition { Height = GridLength.Auto });

        Grid heading = new() { Padding = new Thickness(25, 17, 25, 15), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        heading.Children.Add(new TextBlock { Text = "Tasks", FontSize = 27, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        StackPanel headingActions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        Button pin = IconButton("Pin board on top", "");
        AutomationProperties.SetAutomationId(pin, "pin-board-on-top");
        pin.Click += (_, _) => ToggleTopMost(pin);
        Button more = IconButton("Board menu", "");
        AutomationProperties.SetAutomationId(more, "board-menu");
        more.Click += (_, _) => ShowBoardMenu(more);
        _showAllDueButton = new Button { Content = "Show all tasks", Padding = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_showAllDueButton, "Show all tasks");
        AutomationProperties.SetAutomationId(_showAllDueButton, "show-all-tasks");
        _showAllDueButton.Click += (_, _) => RestoreNormalView();
        headingActions.Children.Add(pin);
        headingActions.Children.Add(more);
        headingActions.Children.Add(_showAllDueButton);
        Grid.SetColumn(headingActions, 1);
        heading.Children.Add(headingActions);
        Grid.SetRow(heading, 0);
        _content.Children.Add(heading);

        _toolbar = new Grid { Padding = new Thickness(25, 0, 25, 0), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 9 };
        _searchBox = new TextBox { PlaceholderText = "Search tasks", Height = 36, Padding = new Thickness(10, 5, 10, 5), Text = View.Search };
        AutomationProperties.SetName(_searchBox, "Search tasks");
        AutomationProperties.SetAutomationId(_searchBox, "task-search");
        _searchBox.TextChanged += (_, _) => { if (!_suppressEvents) { View.Search = _searchBox.Text; ScheduleSave(); ScheduleBoardRefresh(); } };
        Grid.SetColumn(_searchBox, 0);
        _toolbar.Children.Add(_searchBox);
        _sortBox = new ComboBox { Width = 180, Height = 36, ItemsSource = new[] { "Sort: Manual", "Title: A–Z", "Title: Z–A", "Due date: Earliest", "Due date: Latest", "Created: Newest", "Created: Oldest" }, SelectedIndex = SortIndex(View.Sort) };
        AutomationProperties.SetName(_sortBox, "Sort tasks");
        AutomationProperties.SetAutomationId(_sortBox, "task-sort");
        _sortBox.SelectionChanged += (_, _) => { if (!_suppressEvents && _sortBox.SelectedIndex >= 0) { View.Sort = SortAt(_sortBox.SelectedIndex); TrySave(); RefreshBoard(); } };
        _createActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        Button add = PrimaryButton("Task", "");
        AutomationProperties.SetName(add, "Add task");
        AutomationProperties.SetAutomationId(add, "add-task");
        add.Click += (_, _) => StartQuickTask();
        Button mic = PrimaryIconButton("Dictate a task", "\uE720");
        mic.Click += (_, _) =>
        {
            if (_taskButtonMode == RecordingMode.Toggle && !_taskButtonGestureWasHold)
                TaskCaptureRequested?.Invoke();
            _taskButtonGestureWasHold = false;
        };
        mic.PointerPressed += (_, args) =>
        {
            if (BeginTaskButtonHold())
                mic.CapturePointer(args.Pointer);
        };
        mic.PointerReleased += (_, args) =>
        {
            EndTaskButtonHold();
            mic.ReleasePointerCapture(args.Pointer);
        };
        mic.PointerCaptureLost += (_, _) => EndTaskButtonHold();
        _createActions.Children.Add(add);
        _createActions.Children.Add(mic);
        _toolbarActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        _toolbarActions.Children.Add(_sortBox);
        _toolbarActions.Children.Add(_createActions);
        Grid.SetColumn(_toolbarActions, 1);
        _toolbar.Children.Add(_toolbarActions);
        Grid.SetRow(_toolbar, 1);
        _content.Children.Add(_toolbar);

        Grid filters = new() { Padding = new Thickness(25, 15, 25, 4), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 14 };
        ScrollViewer tagScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Left };
        _tagStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tagScroller.Content = _tagStrip;
        StackPanel filterContent = new() { Spacing = 8 };
        filterContent.Children.Add(tagScroller);
        _narrowTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Visibility = Visibility.Collapsed };
        foreach (BoardTaskStatus status in Enum.GetValues<BoardTaskStatus>())
        {
            Button tab = new() { Content = StatusName(status), Tag = status, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            AutomationProperties.SetName(tab, $"Show {StatusName(status)} tasks");
            AutomationProperties.SetAutomationId(tab, $"status-tab-{status.ToString().ToLowerInvariant()}");
            tab.Click += (_, _) =>
            {
                _activeNarrowStatus = status;
                View.NarrowStatus = status;
                PersistTask();
                UpdateResponsiveLayout();
            };
            _narrowTabs.Children.Add(tab);
        }
        filterContent.Children.Add(_narrowTabs);
        filters.Children.Add(filterContent);
        _archiveCheck = new CheckBox { Content = "Show archived", IsChecked = View.ShowArchived, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_archiveCheck, "Show archived tasks");
        AutomationProperties.SetAutomationId(_archiveCheck, "show-archived");
        _archiveCheck.Checked += (_, _) => { if (!_suppressEvents) { View.ShowArchived = true; TrySave(); RefreshBoard(); } };
        _archiveCheck.Unchecked += (_, _) => { if (!_suppressEvents) { View.ShowArchived = false; TrySave(); RefreshBoard(); } };
        Grid.SetColumn(_archiveCheck, 1);
        filters.Children.Add(_archiveCheck);
        Grid.SetRow(filters, 2);
        _content.Children.Add(filters);

        _boardGrid = new Grid { Padding = new Thickness(25, 13, 25, 0), ColumnSpacing = 18 };
        Grid.SetRow(_boardGrid, 3);
        _content.Children.Add(_boardGrid);

        Grid footer = new() { Padding = new Thickness(25, 6, 25, 6), BorderBrush = DividerBrush(), BorderThickness = new Thickness(0, 1, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        _footerSaveStatus = new TextBlock { Text = "Saved on this device", Foreground = SecondaryBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(_footerSaveStatus);
        _retrySaveButton = new Button { Content = "Retry save", Padding = new Thickness(8, 2, 8, 2), Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(_retrySaveButton, "Retry saving task board");
        _retrySaveButton.Click += (_, _) => FlushPendingSave();
        Grid.SetColumn(_retrySaveButton, 1);
        footer.Children.Add(_retrySaveButton);
        var orderHint = new TextBlock { Text = "Drag tasks to reorder · drag tags to assign", Foreground = SecondaryBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(orderHint, 2);
        footer.Children.Add(orderHint);
        Grid.SetRow(footer, 4);
        _content.Children.Add(footer);

        _scrim = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 0, 0, 0)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        AutomationProperties.SetName(_scrim, "Close task details overlay");
        AutomationProperties.SetAutomationId(_scrim, "close-task-details-overlay");
        Grid.SetRowSpan(_scrim, 5);
        _content.Children.Add(_scrim);
        _drawer = new Border { Width = _drawerWidth, HorizontalAlignment = HorizontalAlignment.Right, Background = DrawerSurfaceBrush(), BorderBrush = DividerBrush(), BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed, Padding = new Thickness(24, 0, 24, 0) };
        Grid.SetRowSpan(_drawer, 5);
        _content.Children.Add(_drawer);
        _drawerResizeGrip = new Border { Width = 8, HorizontalAlignment = HorizontalAlignment.Right, Background = TransparentBrush(), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_drawerResizeGrip, "Resize task details");
        AutomationProperties.SetAutomationId(_drawerResizeGrip, "resize-task-drawer");
        Grid.SetRowSpan(_drawerResizeGrip, 5);
        _drawerResizeGrip.PointerPressed += DrawerResizePressed;
        _drawerResizeGrip.PointerMoved += DrawerResizeMoved;
        _drawerResizeGrip.PointerReleased += DrawerResizeReleased;
        _content.Children.Add(_drawerResizeGrip);
        _taskSavedHost = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(25, 10, 25, 0),
            MaxWidth = 540,
            Visibility = Visibility.Collapsed
        };
        _taskSavedBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = InfoBarSeverity.Success,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _taskSavedUndo = new Button { Content = "Undo", Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        AutomationProperties.SetName(_taskSavedUndo, "Undo saved task");
        AutomationProperties.SetAutomationId(_taskSavedUndo, "undo-saved-task");
        _taskSavedBar.CloseButtonClick += (_, _) => HideTaskSavedNotice();
        _taskSavedUndo.Click += UndoCapturedTask;
        Grid.SetColumn(_taskSavedUndo, 1);
        _taskSavedHost.Children.Add(_taskSavedBar);
        _taskSavedHost.Children.Add(_taskSavedUndo);
        Grid.SetRow(_taskSavedHost, 1);
        Root.Children.Add(_taskSavedHost);
        RefreshBoard();
    }

    private void EnsureBoardColumns()
    {
        if (_lists.Count > 0)
            return;
        foreach (BoardTaskStatus status in Enum.GetValues<BoardTaskStatus>())
        {
            _boardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new Grid { RowDefinitions = {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } } };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(3, 0, 3, 11) };
            header.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = StatusBrush(status), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = StatusName(status), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
            _counts[status] = new TextBlock { Foreground = SecondaryBrush(), FontSize = 11.5 };
            header.Children.Add(_counts[status]);
            column.Children.Add(header);
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsTabStop = false,
                ItemTemplate = (DataTemplate)Root.Resources["TaskCardTemplate"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                AllowDrop = true
            };
            var containerStyle = new Style(typeof(ListViewItem));
            containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            containerStyle.Setters.Add(new Setter(Control.IsTabStopProperty, false));
            list.ItemContainerStyle = containerStyle;
            AutomationProperties.SetName(list, StatusName(status) + " tasks");
            AutomationProperties.SetAutomationId(list, $"task-list-{status.ToString().ToLowerInvariant()}");
            list.DragOver += (_, args) =>
            {
                if (CanAcceptTaskDrop(args, status))
                {
                    args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                    HighlightDropColumn(status);
                }
            };
            list.Drop += (_, args) => HandleDropAsync(status, null, args);
            _lists[status] = list;
            Grid.SetRow(list, 2);
            column.Children.Add(list);
            if (status == BoardTaskStatus.ToDo)
            {
                _quickEntryHost = new Border();
                Grid.SetRow(_quickEntryHost, 1);
                column.Children.Add(_quickEntryHost);
            }
            var surface = new Border
            {
                BorderBrush = TransparentBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = column
            };
            _columnSurfaces[status] = surface;
            Grid.SetColumn(surface, (int)status);
            _boardGrid.Children.Add(surface);
        }
    }

    private void RefreshBoard()
    {
        if (_boardGrid is null)
            return;
        EnsureBoardColumns();
        _tagStrip.Children.Clear();
        _tagStrip.Children.Add(TagFilterButton("All tasks", null));
        foreach (BoardTag tag in _document.Tags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase))
            _tagStrip.Children.Add(TagFilterButton(tag.Name, tag.Id, tag.Color));

        bool filtered = !string.IsNullOrWhiteSpace(View.Search) || View.TagFilter.HasValue || _showDueOnly;
        foreach (BoardTaskStatus status in Enum.GetValues<BoardTaskStatus>())
        {
            int total = _document.Tasks.Count(task => !task.DeletedUtc.HasValue && !task.ArchivedUtc.HasValue && task.Status == status);
            IReadOnlyList<BoardTask> visible = _showDueOnly
                ? TaskBoardQuery.DueOrOverdueOpenTasks(_document, DateOnly.FromDateTime(DateTime.Now)).Where(task => task.Status == status).ToArray()
                : TaskBoardQuery.VisibleTasks(_document, status, View.Search, View.TagFilter, false);
            _counts[status].Text = filtered ? $"{visible.Count}/{total}" : visible.Count.ToString();
            BoardTask[] desired = status == BoardTaskStatus.Done && View.ShowArchived && !_showDueOnly
                ? visible.Concat(TaskBoardQuery.ArchivedTasks(_document, View.Search, View.TagFilter)).ToArray()
                : visible.ToArray();
            ListView list = _lists[status];
            list.Footer = desired.Length == 0 ? new TextBlock { Text = "No tasks", Foreground = SecondaryBrush(), Margin = new Thickness(8, 25, 8, 10) } : null;
            var previous = list.ItemsSource as BoardTask[] ?? [];
            if (!previous.Select(task => task.Id).SequenceEqual(desired.Select(task => task.Id)))
            {
                ScrollViewer? scroll = VisualDescendants<ScrollViewer>(list).FirstOrDefault();
                double offset = scroll?.VerticalOffset ?? 0;
                Guid? focused = VisualDescendants<Button>(list)
                    .FirstOrDefault(button => button.FocusState != FocusState.Unfocused && button.Tag is Guid)?.Tag as Guid?;
                list.ItemsSource = desired;
                _dispatcher.TryEnqueue(() =>
                {
                    scroll?.ChangeView(null, offset, null, disableAnimation: true);
                    if (focused is Guid id)
                        FocusTaskCard(id);
                });
            }
            foreach (ContentControl host in VisualDescendants<ContentControl>(list).ToArray())
                if (host.Name == "TaskCardHost")
                    RenderTaskCardHost(host);
        }
        if (_quickEntryActive && _quickEntryHost.Child is null)
            _quickEntryHost.Child = CreateQuickEntry();
        else if (!_quickEntryActive)
            _quickEntryHost.Child = null;
        UpdateResponsiveLayout();
        if (_selectedTask is Guid selected)
            UpdateSelectedOutline(selected);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private void TaskCardHostLoaded(object sender, RoutedEventArgs args) => RenderTaskCardHost((ContentControl)sender);
    private void TaskCardHostChanged(FrameworkElement sender, DataContextChangedEventArgs args) => RenderTaskCardHost((ContentControl)sender);

    private void RenderTaskCardHost(ContentControl host)
    {
        if (host.DataContext is not BoardTask task)
            return;
        var appearance = (task.Id, task.Title, task.Status, task.DueDate, task.ArchivedUtc,
            string.Join("\n", task.TagIds.Select(id => _document.Tags.FirstOrDefault(tag => tag.Id == id))
                .Select(tag => $"{tag?.Id}:{tag?.Name}:{tag?.Color}")));
        if (Equals(host.Tag, appearance))
            return;
        host.Tag = appearance;
        Button replacement = CreateCard(task, task.ArchivedUtc.HasValue);
        if (host.Content is Button card && card.Tag is Guid id && id == task.Id
            && card.CanDrag == !task.ArchivedUtc.HasValue)
        {
            object content = replacement.Content;
            replacement.Content = null;
            card.Content = content;
            AutomationProperties.SetName(card, AutomationProperties.GetName(replacement));
        }
        else
            host.Content = replacement;
    }

    private void ScheduleBoardRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void FocusTaskCard(Guid id)
    {
        foreach (ListView list in _lists.Values)
        {
            if (list.ItemsSource is not BoardTask[] tasks || tasks.FirstOrDefault(task => task.Id == id) is not BoardTask task)
                continue;
            Button? card = VisualDescendants<Button>(list).FirstOrDefault(button => button.Tag is Guid cardId && cardId == id);
            if (card is not null)
                card.Focus(FocusState.Programmatic);
            else
            {
                list.ScrollIntoView(task);
                _dispatcher.TryEnqueue(() => VisualDescendants<Button>(list)
                    .FirstOrDefault(button => button.Tag is Guid cardId && cardId == id)?.Focus(FocusState.Programmatic));
            }
            return;
        }
    }

    private Button CreateCard(BoardTask task, bool archived = false)
    {
        var card = new Button { Tag = task.Id, Background = SurfaceBrush(), BorderBrush = task.Id == _selectedTask ? AccentBrush() : DividerBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 11, 13, 9), Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, AllowDrop = !archived, CanDrag = !archived };
        AutomationProperties.SetName(card, $"Task {task.Title}, {(archived ? "Archived" : StatusName(task.Status))}");
        AutomationProperties.SetAutomationId(card, $"task-{task.Id:N}");
        card.Click += (_, _) => OpenDrawer(task.Id);
        if (!archived)
        {
            // Button consumes pointer input, so start the native drag after a real drag gesture.
            Windows.Foundation.Point? dragOrigin = null;
            card.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                DependencyObject? source = args.OriginalSource as DependencyObject;
                while (source is not null && source is not Button)
                    source = VisualTreeHelper.GetParent(source);
                var point = args.GetCurrentPoint(card);
                dragOrigin = source == card && point.Properties.IsLeftButtonPressed ? point.Position : null;
            }), true);
            card.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, args) =>
            {
                var point = args.GetCurrentPoint(card);
                if (dragOrigin is not { } origin || !point.Properties.IsLeftButtonPressed || _draggingTask.HasValue
                    || Math.Max(Math.Abs(point.Position.X - origin.X), Math.Abs(point.Position.Y - origin.Y)) < 8)
                    return;
                dragOrigin = null;
                _draggingTask = task.Id;
                card.ReleasePointerCaptures();
                try { await card.StartDragAsync(point); }
                catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    SetSaveStatus("Could not start dragging · use Task actions", failed: true);
                    System.Diagnostics.Trace.WriteLine($"WinWispr task drag failed: {exception}");
                }
                finally { _draggingTask = null; }
            }), true);
            card.PointerCaptureLost += (_, _) => dragOrigin = null;
            card.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => dragOrigin = null), true);
            card.DragStarting += (_, args) =>
            {
                _draggingTask = task.Id;
                args.Data.SetText(task.Id.ToString("D"));
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };
            card.DropCompleted += (_, _) => { dragOrigin = null; _draggingTask = null; ClearDropColumn(); };
            card.DragOver += (_, args) =>
            {
                if (CanAcceptTagDrop(args, task) || CanAcceptTaskDrop(args, task.Status))
                {
                    args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                    HighlightDropColumn(task.Status);
                }
            };
            card.Drop += (_, args) => HandleDropAsync(task.Status, task.Id, args);
        }
        var body = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } } };
        var title = new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
        ToolTipService.SetToolTip(title, task.Title);
        body.Children.Add(title);
        StackPanel tags = new() { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 9, 0, 0), MaxWidth = 320 };
        if (archived)
            tags.Children.Add(new TextBlock { Text = "Archived", Foreground = SecondaryBrush(), FontSize = 11 });
        foreach (Guid tagId in task.TagIds.Take(2))
        {
            BoardTag? tag = _document.Tags.FirstOrDefault(item => item.Id == tagId);
            if (tag is null)
                continue;
            Button tagButton = TagChip(tag.Name, tag.Id, tag.Color);
            tags.Children.Add(tagButton);
        }
        if (task.TagIds.Count > 2)
            tags.Children.Add(new TextBlock { Text = $"+{task.TagIds.Count - 2}", Foreground = SecondaryBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(tags, 1);
        body.Children.Add(tags);
        Grid bottom = new() { Margin = new Thickness(0, 5, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        if (task.DueDate is DateOnly due)
        {
            bool overdue = !archived && task.Status != BoardTaskStatus.Done && due < DateOnly.FromDateTime(DateTime.Now);
            bottom.Children.Add(new TextBlock { Text = (overdue ? "! " : "") + FormatDue(due), Foreground = overdue ? ErrorBrush() : SecondaryBrush(), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        }
        Button menu = IconButton("Task actions", "");
        AutomationProperties.SetAutomationId(menu, $"task-actions-{task.Id:N}");
        menu.Width = 23;
        menu.Height = 23;
        menu.Click += (_, _) => ShowTaskMenu(menu, task);
        Grid.SetColumn(menu, 1);
        bottom.Children.Add(menu);
        Grid.SetRow(bottom, 2);
        body.Children.Add(bottom);
        card.Content = body;
        return card;
    }

    private void OpenDrawer(Guid id)
    {
        BoardTask? task = _document.Tasks.FirstOrDefault(item => item.Id == id && !item.DeletedUtc.HasValue);
        if (task is null)
            return;
        _selectedTask = id;
        _editingDescription = false;
        _scrim.Visibility = Visibility.Visible;
        _drawer.Visibility = Visibility.Visible;
        _drawerResizeGrip.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
        _drawer.Width = _drawerWidth;
        UpdateDrawerResizeGrip();
        BuildDrawer(task);
        RefreshBoard();
    }

    private void BuildDrawer(BoardTask task)
    {
        _drawer.Child = null;
        _descriptionEditor = null;
        Grid layout = new() { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } } };
        Grid top = new() { Padding = new Thickness(0, 4, 0, 8), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        bool failed = _lastSaveFailed || _store.LoadWarning is not null;
        _saveStatus = new TextBlock { Text = failed ? _lastSaveText : "Saved", Foreground = failed ? ErrorBrush() : SecondaryBrush(), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_saveStatus);
        Button close = IconButton("Close task details", "");
        AutomationProperties.SetAutomationId(close, "close-task-details");
        close.Click += (_, _) => CloseDrawer();
        Grid.SetColumn(close, 1);
        top.Children.Add(close);
        layout.Children.Add(top);

        ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        StackPanel body = new() { Spacing = 0, Padding = new Thickness(0, 4, 0, 20) };
        TextBox title = new() { AcceptsReturn = true, Text = _titleEdits.TryGetValue(task.Id, out string? pendingTitle) ? pendingTitle : task.Title, TextWrapping = TextWrapping.Wrap, FontSize = 23, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 72 };
        AutomationProperties.SetName(title, "Task title");
        AutomationProperties.SetAutomationId(title, "task-title");
        title.TextChanged += (sender, _) =>
        {
            if (_suppressEvents)
                return;
            string text = ((TextBox)sender).Text;
            _titleEdits[task.Id] = text;
            ScheduleSave();
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 400)
            {
                task.Title = text;
                TouchTask(task);
                RefreshBoard();
            }
            else
                SetSaveStatus(string.IsNullOrWhiteSpace(text) ? "Title is required" : "Task title is too long", failed: true);
        };
        body.Children.Add(title);

        body.Children.Add(MetadataLabel("Status"));
        ComboBox status = new() { ItemsSource = Enum.GetValues<BoardTaskStatus>().Select(StatusName).ToArray(), SelectedIndex = (int)task.Status, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(status, "Task status");
        AutomationProperties.SetAutomationId(status, "task-status");
        status.SelectionChanged += (_, _) => { if (!_suppressEvents && status.SelectedIndex >= 0) { TaskBoardOperations.Move(task, (BoardTaskStatus)status.SelectedIndex, DateTimeOffset.UtcNow); PersistTask(); RefreshBoard(); } };
        body.Children.Add(status);

        body.Children.Add(MetadataLabel("Due date"));
        StackPanel dateRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        CalendarDatePicker datePicker = new() { Date = task.DueDate is DateOnly due ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue)) : null, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(datePicker, "Task due date");
        AutomationProperties.SetAutomationId(datePicker, "task-due-date");
        datePicker.DateChanged += (_, args) => { if (!_suppressEvents) { task.DueDate = args.NewDate is DateTimeOffset date ? DateOnly.FromDateTime(date.DateTime) : null; TouchTask(task); PersistTask(); RefreshBoard(); } };
        dateRow.Children.Add(datePicker);
        foreach ((string text, DateOnly? date) in new[] { ("Today", DateOnly.FromDateTime(DateTime.Now)), ("Tomorrow", DateOnly.FromDateTime(DateTime.Now).AddDays(1)), ("No date", (DateOnly?)null) })
        {
            Button quick = LinkButton(text);
            AutomationProperties.SetName(quick, $"Set due date {text.ToLowerInvariant()}");
            quick.Click += (_, _) => { _suppressEvents = true; datePicker.Date = date is DateOnly selected ? new DateTimeOffset(selected.ToDateTime(TimeOnly.MinValue)) : null; _suppressEvents = false; task.DueDate = date; TouchTask(task); PersistTask(); RefreshBoard(); };
            dateRow.Children.Add(quick);
        }
        body.Children.Add(dateRow);

        body.Children.Add(MetadataLabel("Tags"));
        StackPanel tagEditor = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Guid tagId in task.TagIds.ToArray())
        {
            BoardTag? tag = _document.Tags.FirstOrDefault(item => item.Id == tagId);
            if (tag is null)
                continue;
            Button chip = TagChip(tag.Name, tag.Id, tag.Color, removable: true);
            chip.Click += (_, _) => { task.TagIds.Remove(tag.Id); TouchTask(task); PersistTask(); BuildDrawer(task); RefreshBoard(); };
            tagEditor.Children.Add(chip);
        }
        AutoSuggestBox tagInput = new() { PlaceholderText = "Add a tag", Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(tagInput, "Task tags");
        AutomationProperties.SetAutomationId(tagInput, "task-tags");
        tagInput.ItemsSource = _document.Tags.Select(tag => tag.Name).ToArray();
        // TextChanged arrives asynchronously, possibly after AddTag rebuilt the drawer and the captured
        // wrapper of the detached box was released; only the sender is guaranteed to be usable.
        tagInput.TextChanged += (sender, _) =>
        {
            string query = TaskDraftParser.Normalize(sender.Text);
            sender.ItemsSource = _document.Tags.Select(tag => tag.Name)
                .Where(name => query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        };
        tagInput.SuggestionChosen += (sender, args) => AddTag(task, args.SelectedItem as string, sender);
        tagInput.QuerySubmitted += (sender, args) => AddTag(task, args.QueryText, sender);
        tagEditor.Children.Add(tagInput);
        body.Children.Add(tagEditor);

        if (task.TagIds.Count == 0)
        {
            IReadOnlyList<Guid> suggestedIds = TaskDraftParser.Parse(
                $"{task.Title}\n{task.Description}",
                _document.Tags,
                DateOnly.FromDateTime(DateTime.Now)).SuggestedTagIds;
            if (suggestedIds.Count > 0)
            {
                body.Children.Add(MetadataLabel("Suggested tags"));
                StackPanel suggestions = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
                foreach (Guid tagId in suggestedIds)
                {
                    BoardTag? tag = _document.Tags.FirstOrDefault(item => item.Id == tagId);
                    if (tag is null)
                        continue;
                    Button suggestion = new() { Content = tag.Name, Background = TagBrush(tag.Color), BorderThickness = new Thickness(0), Padding = new Thickness(8, 4, 8, 4) };
                    AutomationProperties.SetName(suggestion, $"Suggested task tag {tag.Name}");
                    AutomationProperties.SetAutomationId(suggestion, $"suggested-task-tag-{tag.Id:N}");
                    suggestion.Click += (_, _) => AddTag(task, tag.Name, tagInput);
                    suggestions.Children.Add(suggestion);
                }
                if (suggestions.Children.Count > 0)
                    body.Children.Add(suggestions);
            }
        }

        Grid descriptionHeader = new() { Margin = new Thickness(0, 24, 0, 10), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        descriptionHeader.Children.Add(new TextBlock { Text = "Description", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
        descriptionHeader.Children.Add(new TextBlock { Text = _editingDescription ? "Markdown supported" : string.IsNullOrWhiteSpace(task.Description) ? "Click to add" : "Click to edit", Foreground = SecondaryBrush(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
        body.Children.Add(descriptionHeader);
        string descriptionText = task.Description;
        if (_editingDescription)
        {
            // AcceptsReturn must precede Text: a single-line TextBox keeps only the first line it is given.
            TextBox description = new() { AcceptsReturn = true, Text = descriptionText, TextWrapping = TextWrapping.Wrap, MinHeight = 190 };
            _descriptionEditor = description;
            AutomationProperties.SetName(description, "Description in Markdown");
            AutomationProperties.SetAutomationId(description, "task-description");
            description.TextChanged += (sender, _) =>
            {
                if (_suppressEvents)
                    return;
                ApplyDescriptionEdit(task, ((TextBox)sender).Text);
            };
            description.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; _editingDescription = false; BuildDrawer(task); } };
            body.Children.Add(description);
            if (descriptionText.Length > 100000)
                body.Children.Add(new TextBlock { Text = "Description is too long to save; your input is retained until you shorten it.", Foreground = ErrorBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            _dispatcher.TryEnqueue(() => description.Focus(FocusState.Programmatic));
        }
        else
        {
            bool empty = string.IsNullOrWhiteSpace(descriptionText);
            Button description = new()
            {
                Content = RenderMarkdown(descriptionText),
                Background = empty ? SurfaceBrush() : TransparentBrush(),
                BorderBrush = empty ? DividerBrush() : TransparentBrush(),
                BorderThickness = new Thickness(empty ? 1 : 0),
                CornerRadius = new CornerRadius(empty ? 6 : 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = empty ? VerticalAlignment.Top : VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = empty ? 96 : 0,
                Padding = empty ? new Thickness(12) : new Thickness(0)
            };
            AutomationProperties.SetName(description, "Edit description");
            AutomationProperties.SetAutomationId(description, "edit-task-description");
            description.Click += (_, _) => { _editingDescription = true; BuildDrawer(task); };
            body.Children.Add(description);
            if (descriptionText.Length > 100000)
                body.Children.Add(new TextBlock { Text = "Description is too long to save; reopen editing to shorten it.", Foreground = ErrorBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        scroll.Content = body;
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid footer = new() { Padding = new Thickness(0, 9, 0, 7), BorderBrush = DividerBrush(), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Children.Add(new TextBlock { Text = $"Created {task.CreatedUtc.ToLocalTime():MMM d}", Foreground = SecondaryBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        _drawer.Child = layout;
    }

    private void CloseDrawer()
    {
        FlushPendingSave();
        if (_editingDescription)
        {
            _editingDescription = false;
            if (_selectedTask is Guid id && _document.Tasks.FirstOrDefault(task => task.Id == id) is BoardTask task)
                BuildDrawer(task);
            return;
        }
        Guid? closedTask = _selectedTask;
        _selectedTask = null;
        _drawer.Visibility = Visibility.Collapsed;
        _scrim.Visibility = Visibility.Collapsed;
        _drawerResizeGrip.Visibility = Visibility.Collapsed;
        RefreshBoard();
        _dispatcher.TryEnqueue(() =>
        {
            Guid? target = closedTask ?? _lists.Values.SelectMany(list => list.Items.OfType<BoardTask>()).FirstOrDefault()?.Id;
            if (target is Guid id)
                FocusTaskCard(id);
        });
    }

    private void ContentPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_drawer.Visibility != Visibility.Visible)
            return;
        DependencyObject? source = args.OriginalSource as DependencyObject;
        if (IsDescendantOf(source, _drawer) || IsDescendantOf(source, _drawerResizeGrip) || IsTaskCardSource(source))
            return;
        CloseDrawer();
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static bool IsTaskCardSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button && button.Tag is Guid)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && _drawer.Visibility == Visibility.Visible)
        {
            args.Handled = true;
            CloseDrawer();
        }
    }

    private void DrawerResizePressed(object sender, PointerRoutedEventArgs args)
    {
        if (_narrow || _drawer.Visibility != Visibility.Visible)
            return;
        _resizingDrawer = true;
        _drawerResizeStartWidth = _drawerWidth;
        _drawerResizeStartX = args.GetCurrentPoint(_content).Position.X;
        _drawerResizeGrip.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void DrawerResizeMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingDrawer)
            return;
        double currentX = args.GetCurrentPoint(_content).Position.X;
        SetDrawerWidth(_drawerResizeStartWidth + (int)Math.Round(_drawerResizeStartX - currentX));
        args.Handled = true;
    }

    private void DrawerResizeReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingDrawer)
            return;
        _resizingDrawer = false;
        _drawerResizeGrip.ReleasePointerCapture(args.Pointer);
        PersistTask();
        DrawerWidthChanged?.Invoke(_drawerWidth);
        args.Handled = true;
    }

    private void SetDrawerWidth(int width)
    {
        _drawerWidth = Math.Clamp(width, 300, 700);
        _drawer.Width = _drawerWidth;
        UpdateDrawerResizeGrip();
    }

    private void UpdateDrawerResizeGrip()
    {
        if (_drawerResizeGrip is null)
            return;
        _drawerResizeGrip.Margin = new Thickness(0, 0, Math.Max(0, _drawerWidth - 4), 0);
    }

    private void AddTag(BoardTask task, string? value, AutoSuggestBox input)
    {
        string name = TaskDraftParser.Normalize(value ?? string.Empty);
        if (name.Length == 0)
            return;
        if (name.Length > 80)
        {
            SetSaveStatus("Tag names are limited to 80 characters", failed: true);
            return;
        }
        BoardTag? tag = _document.Tags.FirstOrDefault(item => string.Equals(TaskDraftParser.Normalize(item.Name), name, StringComparison.OrdinalIgnoreCase));
        if (tag is null)
        {
            tag = new BoardTag { Name = name, Color = ColorForIndex(_document.Tags.Count) };
            _document.Tags.Add(tag);
        }
        TaskBoardOperations.AssignTag(task, tag.Id, DateTimeOffset.UtcNow);
        input.IsSuggestionListOpen = false;
        input.Text = string.Empty;
        PersistTask();
        BuildDrawer(task);
        RefreshBoard();
    }

    private TextBox CreateQuickEntry()
    {
        var input = new TextBox { Text = _quickEntryText, PlaceholderText = "What needs to be done?", Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(input, "New task title");
        AutomationProperties.SetAutomationId(input, "new-task-title");
        input.TextChanged += (sender, _) => { _quickEntryText = ((TextBox)sender).Text; ScheduleSave(); };
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(input.Text))
            {
                args.Handled = true;
                SubmitQuickTask();
            }
            else if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                _quickEntryActive = false;
                _quickEntryText = string.Empty;
                ScheduleSave();
                RefreshBoard();
            }
        };
        _dispatcher.TryEnqueue(() => input.Focus(FocusState.Programmatic));
        return input;
    }

    private bool SubmitQuickTask()
    {
        if (!_quickEntryActive || string.IsNullOrWhiteSpace(_quickEntryText) || _quickEntryText.Trim().Length > 400)
        {
            SetSaveStatus("Task title must contain between 1 and 400 characters", failed: true);
            return false;
        }
        BoardTask created = TaskBoardOperations.CreateTask(_document, _quickEntryText, "", "", null, [], DateTimeOffset.UtcNow);
        _quickEntryActive = false;
        if (!PersistTask())
        {
            _document.Tasks.Remove(created);
            _quickEntryActive = true;
            TryWriteRecoveryCopy();
            SetSaveStatus("Could not save the task. Your text remains here; retry when storage is available.", failed: true);
            return false;
        }
        _quickEntryText = string.Empty;
        RefreshBoard();
        OpenDrawer(created.Id);
        return true;
    }

    private void StartQuickTask()
    {
        if (_narrow && _activeNarrowStatus != BoardTaskStatus.ToDo)
        {
            _activeNarrowStatus = BoardTaskStatus.ToDo;
            View.NarrowStatus = BoardTaskStatus.ToDo;
            _suppressEvents = true;
            View.Search = string.Empty;
            _searchBox.Text = string.Empty;
            _suppressEvents = false;
            RefreshBoard();
        }
        _quickEntryActive = true;
        RefreshBoard();
        _dispatcher.TryEnqueue(() =>
        {
            if (_quickEntryHost.Child is TextBox input)
                input.Focus(FocusState.Programmatic);
        });
    }

    private void ShowBoardMenu(FrameworkElement target)
    {
        var menu = new MenuFlyout();
        var tags = new MenuFlyoutItem { Text = "Manage tags" };
        AutomationProperties.SetAutomationId(tags, "board-menu-manage-tags");
        tags.Click += (_, _) => ShowTagManager();
        menu.Items.Add(tags);
        var trash = new MenuFlyoutItem { Text = "Trash" };
        AutomationProperties.SetAutomationId(trash, "board-menu-trash");
        trash.Click += (_, _) => ShowTrash();
        menu.Items.Add(trash);
        if (HasTaskDraft)
        {
            var resume = new MenuFlyoutItem { Text = "Resume task draft" };
            AutomationProperties.SetAutomationId(resume, "board-menu-resume-draft");
            resume.Click += (_, _) => TaskDraftResumeRequested?.Invoke();
            menu.Items.Add(resume);
        }
        if (_showDueOnly)
        {
            var normal = new MenuFlyoutItem { Text = "Show all tasks" };
            AutomationProperties.SetAutomationId(normal, "board-menu-show-all");
            normal.Click += (_, _) => RestoreNormalView();
            menu.Items.Add(normal);
        }
        var export = new MenuFlyoutItem { Text = "Save recovery copy elsewhere" };
        AutomationProperties.SetAutomationId(export, "board-menu-export-recovery");
        export.Click += (_, _) => _ = ExportRecoveryCopyAsync();
        menu.Items.Add(export);
        var import = new MenuFlyoutItem { Text = "Import recovery copy" };
        AutomationProperties.SetAutomationId(import, "board-menu-import-recovery");
        import.Click += (_, _) => _ = ImportRecoveryCopyAsync();
        menu.Items.Add(import);
        if (_store.RecoveryNeedsChoice)
        {
            var keep = new MenuFlyoutItem { Text = "Keep current board and discard preserved recovery" };
            AutomationProperties.SetAutomationId(keep, "board-menu-discard-recovery");
            keep.Click += (_, _) => DiscardStaleRecoveryCopy();
            menu.Items.Add(keep);
        }
        if (!_recoveryAcknowledged && _store.LoadWarning is not null && !_store.RecoveryNeedsChoice)
        {
            var acknowledge = new MenuFlyoutItem { Text = "Acknowledge recovered task data" };
            AutomationProperties.SetAutomationId(acknowledge, "board-menu-acknowledge-recovery");
            acknowledge.Click += (_, _) => AcknowledgeRecoveredData();
            menu.Items.Add(acknowledge);
        }
        menu.ShowAt(target);
    }

    private async Task ExportRecoveryCopyAsync()
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = "winwispr-task-recovery.json",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeChoices.Add("JSON recovery copy", new[] { ".json" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                SetSaveStatus("Recovery export cancelled", failed: true);
                return;
            }
            DrainWrite();
            _store.SaveRecoveryTo(file.Path, _document, _descriptionEdits, _titleEdits,
                _quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText) ? _quickEntryText : null);
            _exportedRecoveryIdentity = CurrentRecoveryIdentity();
            SetSaveStatus("Recovery copy exported", failed: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetSaveStatus("Recovery export failed · retry", failed: true);
            System.Diagnostics.Trace.WriteLine($"WinWispr recovery export failed: {exception.GetType().Name}");
        }
    }

    private async Task ImportRecoveryCopyAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
                return;
            if (!TaskBoardStore.TryLoadRecovery(file.Path, out TaskBoardRecoveryFile? recovery, out _)
                || recovery is null)
            {
                SetSaveStatus("Recovery import failed · invalid copy", failed: true);
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Import recovery copy?",
                Content = $"This will restore {recovery.Document.Tasks.Count} tasks and {recovery.Document.Tags.Count} tags from the selected copy. Any existing board data will be backed up first.",
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot
            };
            AutomationProperties.SetAutomationId(confirm, "recovery-import-dialog");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FlushPendingSave();
            if (HasPendingDurableData)
            {
                SetSaveStatus("Recovery import paused · save the current changes first", failed: true);
                return;
            }
            _store.CreateRecoveryImportBackup();

            TaskBoardDocument imported = recovery.Document.Copy();
            imported.Draft = recovery.Draft?.Copy() ?? imported.Draft;
            _document.Version = imported.Version;
            _document.Revision = imported.Revision;
            _document.Tasks = imported.Tasks;
            _document.Tags = imported.Tags;
            _document.Draft = imported.Draft;
            _document.View = imported.View;
            _document.LastSummaryLocalDate = imported.LastSummaryLocalDate;
            _descriptionEdits.Clear();
            foreach ((Guid taskId, string description) in recovery.PendingDescriptions)
                _descriptionEdits[taskId] = description;
            _titleEdits.Clear();
            foreach ((Guid taskId, string title) in recovery.PendingTitles)
                _titleEdits[taskId] = title;
            _quickEntryText = recovery.PendingQuickEntry ?? string.Empty;
            _quickEntryActive = !string.IsNullOrWhiteSpace(_quickEntryText);
            _store.ResetRecoveryState();
            _recoveryAcknowledged = true;
            _selectedTask = null;
            _editingDescription = false;
            _descriptionEditor = null;
            _showDueOnly = false;
            _drawer.Visibility = Visibility.Collapsed;
            _scrim.Visibility = Visibility.Collapsed;
            _drawerResizeGrip.Visibility = Visibility.Collapsed;
            if (_showAllDueButton is not null)
                _showAllDueButton.Visibility = Visibility.Collapsed;
            _activeNarrowStatus = View.NarrowStatus;
            _drawerWidth = Math.Clamp(View.DrawerWidth, 300, 700);
            _suppressEvents = true;
            _searchBox.Text = View.Search;
            _sortBox.SelectedIndex = SortIndex(View.Sort);
            _archiveCheck.IsChecked = View.ShowArchived;
            _suppressEvents = false;
            _drawer.Width = _drawerWidth;
            UpdateDrawerResizeGrip();
            foreach (ListView list in _lists.Values)
                list.ItemsSource = null;
            RefreshBoard();
            if (PersistTask())
                SetSaveStatus(HasPendingDurableData ? "Recovery copy imported · preserved edits available" : "Recovery copy imported", failed: false);
            else
                SetSaveStatus("Recovery copy imported · save failed, recovery copy available", failed: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetSaveStatus("Recovery import failed · current board kept", failed: true);
            System.Diagnostics.Trace.WriteLine($"WinWispr recovery import failed: {exception.GetType().Name}");
        }
    }

    private void ShowTaskMenu(FrameworkElement target, BoardTask task)
    {
        var menu = new MenuFlyout();
        foreach (BoardTaskStatus status in Enum.GetValues<BoardTaskStatus>())
        {
            var item = new MenuFlyoutItem { Text = $"Move to {StatusName(status)}", IsEnabled = task.Status != status };
            AutomationProperties.SetAutomationId(item, $"task-menu-move-{status.ToString().ToLowerInvariant()}");
            item.Click += (_, _) => { TaskBoardOperations.Move(task, status, DateTimeOffset.UtcNow); PersistTask(); RefreshBoard(); };
            menu.Items.Add(item);
        }
        var moveUp = new MenuFlyoutItem { Text = "Move up", IsEnabled = View.Sort == BoardTaskSort.Manual && !task.ArchivedUtc.HasValue };
        AutomationProperties.SetAutomationId(moveUp, "task-menu-move-up");
        moveUp.Click += (_, _) => ReorderTask(task, -1);
        menu.Items.Add(moveUp);
        var moveDown = new MenuFlyoutItem { Text = "Move down", IsEnabled = View.Sort == BoardTaskSort.Manual && !task.ArchivedUtc.HasValue };
        AutomationProperties.SetAutomationId(moveDown, "task-menu-move-down");
        moveDown.Click += (_, _) => ReorderTask(task, 1);
        menu.Items.Add(moveDown);
        if (task.Status == BoardTaskStatus.Done)
        {
            var archive = new MenuFlyoutItem { Text = task.ArchivedUtc.HasValue ? "Restore from archive" : "Archive" };
            AutomationProperties.SetAutomationId(archive, task.ArchivedUtc.HasValue ? "task-menu-restore-archive" : "task-menu-archive");
            archive.Click += (_, _) => { if (task.ArchivedUtc.HasValue) TaskBoardOperations.RestoreArchive(task, DateTimeOffset.UtcNow); else TaskBoardOperations.Archive(task, DateTimeOffset.UtcNow); PersistTask(); RefreshBoard(); };
            menu.Items.Add(archive);
        }
        var delete = new MenuFlyoutItem { Text = "Move to trash" };
        AutomationProperties.SetAutomationId(delete, "task-menu-trash");
        delete.Click += (_, _) => { TaskBoardOperations.Delete(task, DateTimeOffset.UtcNow); PersistTask(); CloseDrawer(); RefreshBoard(); };
        menu.Items.Add(delete);
        menu.ShowAt(target);
    }

    private void ReorderTask(BoardTask task, int direction)
    {
        if (View.Sort != BoardTaskSort.Manual || task.ArchivedUtc.HasValue || task.DeletedUtc.HasValue)
            return;
        List<BoardTask> siblings = _document.Tasks
            .Where(item => !item.DeletedUtc.HasValue && !item.ArchivedUtc.HasValue && item.Status == task.Status)
            .OrderBy(item => item.ManualOrder)
            .ThenBy(item => item.CreatedUtc)
            .ToList();
        int index = siblings.FindIndex(item => item.Id == task.Id);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= siblings.Count)
            return;
        for (int orderIndex = 0; orderIndex < siblings.Count; orderIndex++)
            siblings[orderIndex].ManualOrder = (orderIndex + 1) * 10L;
        (siblings[index].ManualOrder, siblings[target].ManualOrder) = (siblings[target].ManualOrder, siblings[index].ManualOrder);
        TouchTask(task);
        PersistTask();
        RefreshBoard();
    }

    private void ShowTagManager()
    {
        var dialog = new ContentDialog { Title = "Manage tags", CloseButtonText = "Done", XamlRoot = Root.XamlRoot };
        AutomationProperties.SetAutomationId(dialog, "tag-manager-dialog");
        StackPanel body = new() { Spacing = 8 };
        TextBox newTagName = new() { PlaceholderText = "New tag name", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(newTagName, "New tag name");
        AutomationProperties.SetAutomationId(newTagName, "new-tag-name");
        Button addTag = new() { Content = "Add", Padding = new Thickness(12, 5, 12, 5) };
        AutomationProperties.SetName(addTag, "Add tag");
        AutomationProperties.SetAutomationId(addTag, "add-tag");
        TextBlock addStatus = new() { Foreground = ErrorBrush(), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        BoardTag? pendingTag = null;
        void SetAddStatus(string text)
        {
            addStatus.Text = text;
            addStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void SetPendingTag(BoardTag? tag)
        {
            pendingTag = tag;
            addTag.Content = tag is null ? "Add" : "Retry";
            AutomationProperties.SetName(addTag, tag is null ? "Add tag" : "Retry saving tag");
            newTagName.IsEnabled = tag is null;
        }
        bool SaveManagedTagChanges()
        {
            bool saved = PersistTask();
            if (saved)
                SetPendingTag(null);
            return saved;
        }

        Grid addRow = new() { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(120) }, new ColumnDefinition { Width = new GridLength(34) } } };
        addRow.Children.Add(newTagName);
        Grid.SetColumn(addTag, 1);
        addTag.HorizontalAlignment = HorizontalAlignment.Right;
        addRow.Children.Add(addTag);
        body.Children.Add(addRow);
        body.Children.Add(addStatus);
        BoardTag? pendingDeleteTag = null;
        TextBlock deleteMessage = new() { Foreground = ErrorBrush(), TextWrapping = TextWrapping.Wrap };
        Button confirmDelete = new() { Content = "Delete", Padding = new Thickness(12, 4, 12, 4) };
        Button cancelDelete = new() { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4) };
        AutomationProperties.SetAutomationId(confirmDelete, "tag-delete-confirm");
        AutomationProperties.SetName(confirmDelete, "Confirm tag deletion");
        AutomationProperties.SetAutomationId(cancelDelete, "tag-delete-cancel");
        AutomationProperties.SetName(cancelDelete, "Cancel tag deletion");
        StackPanel deleteActions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        deleteActions.Children.Add(cancelDelete);
        deleteActions.Children.Add(confirmDelete);
        StackPanel deletePrompt = new() { Spacing = 6, Visibility = Visibility.Collapsed };
        Dictionary<Guid, Grid> tagRows = [];
        AutomationProperties.SetAutomationId(deletePrompt, "tag-delete-confirmation");
        deletePrompt.Children.Add(deleteMessage);
        deletePrompt.Children.Add(deleteActions);
        body.Children.Add(deletePrompt);
        void HideDeletePrompt()
        {
            pendingDeleteTag = null;
            deletePrompt.Visibility = Visibility.Collapsed;
        }
        void ShowDeletePrompt(BoardTag tag)
        {
            pendingDeleteTag = tag;
            int affectedTasks = _document.Tasks.Count(task => task.TagIds.Contains(tag.Id));
            deleteMessage.Text = affectedTasks == 0
                ? $"Delete tag {tag.Name}? This tag is not assigned to any task."
                : $"Delete tag {tag.Name}? This removes it from {affectedTasks} task{(affectedTasks == 1 ? string.Empty : "s")}.";
            deletePrompt.Visibility = Visibility.Visible;
        }
        void DeleteTag(BoardTag tag, Grid row)
        {
            byte[]? recoveryBeforeDelete;
            try
            {
                recoveryBeforeDelete = _store.CaptureRecoveryCopy();
            }
            catch (IOException)
            {
                SetAddStatus("Could not delete the tag because its recovery state could not be read.");
                return;
            }
            catch (UnauthorizedAccessException)
            {
                SetAddStatus("Could not delete the tag because its recovery state could not be read.");
                return;
            }
            int tagIndex = _document.Tags.IndexOf(tag);
            Guid? previousFilter = View.TagFilter;
            bool draftSelected = _document.Draft?.SelectedTagIds.Contains(tag.Id) == true;
            bool draftSuggested = _document.Draft?.SuggestedTagIds.Contains(tag.Id) == true;
            Guid[] assignedTaskIds = _document.Tasks.Where(task => task.TagIds.Contains(tag.Id)).Select(task => task.Id).ToArray();
            _document.Tags.Remove(tag);
            foreach (BoardTask task in _document.Tasks)
                task.TagIds.Remove(tag.Id);
            if (_document.Draft is TaskCaptureDraft draft)
            {
                draft.SelectedTagIds.Remove(tag.Id);
                draft.SuggestedTagIds.Remove(tag.Id);
            }
            if (View.TagFilter == tag.Id)
                View.TagFilter = null;
            if (!SaveManagedTagChanges())
            {
                _document.Tags.Insert(tagIndex, tag);
                foreach (Guid taskId in assignedTaskIds)
                    if (_document.Tasks.FirstOrDefault(task => task.Id == taskId) is BoardTask task)
                        task.TagIds.Add(tag.Id);
                if (_document.Draft is TaskCaptureDraft draftToRestore)
                {
                    if (draftSelected)
                        draftToRestore.SelectedTagIds.Add(tag.Id);
                    if (draftSuggested)
                        draftToRestore.SuggestedTagIds.Add(tag.Id);
                }
                View.TagFilter = previousFilter;
                try
                {
                    _store.RestoreRecoveryCopy(recoveryBeforeDelete);
                }
                catch (IOException)
                {
                    SetAddStatus("Could not delete the tag; the recovery copy could not be rolled back.");
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    SetAddStatus("Could not delete the tag; the recovery copy could not be rolled back.");
                    return;
                }
                SetAddStatus("Could not delete the tag because the board could not be saved.");
                return;
            }
            if (pendingTag?.Id == tag.Id)
            {
                SetPendingTag(null);
                SetAddStatus(string.Empty);
            }
            HideDeletePrompt();
            tagRows.Remove(tag.Id);
            body.Children.Remove(row);
            RefreshBoard();
        }
        cancelDelete.Click += (_, _) => HideDeletePrompt();
        confirmDelete.Click += (_, _) =>
        {
            if (pendingDeleteTag is BoardTag tag && tagRows.TryGetValue(tag.Id, out Grid? row))
                DeleteTag(tag, row);
        };

        void AddTagRow(BoardTag tag)
        {
            Grid row = new() { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(120) }, new ColumnDefinition { Width = new GridLength(34) } } };
            TextBox name = new() { Text = tag.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(name, $"Tag name {tag.Name}");
            AutomationProperties.SetAutomationId(name, $"tag-name-{tag.Id:N}");
            name.LostFocus += (_, _) => { string value = TaskDraftParser.Normalize(name.Text); if (value.Length > 0 && value.Length <= 80 && !_document.Tags.Any(other => other.Id != tag.Id && string.Equals(other.Name, value, StringComparison.OrdinalIgnoreCase))) { tag.Name = value; SaveManagedTagChanges(); RefreshBoard(); } else { name.Text = tag.Name; if (value.Length > 80) SetSaveStatus("Tag names are limited to 80 characters", failed: true); } };
            row.Children.Add(name);
            ComboBox color = new() { Width = 120, Margin = new Thickness(8, 0, 8, 0) };
            string[] colorNames = ["blue", "green", "amber", "purple", "rose"];
            foreach (string colorName in colorNames)
            {
                var option = new ComboBoxItem
                {
                    Tag = colorName,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = TagBrush(colorName) },
                            new TextBlock { Text = colorName }
                        }
                    }
                };
                AutomationProperties.SetName(option, colorName);
                color.Items.Add(option);
            }
            color.SelectedIndex = Array.IndexOf(colorNames, tag.Color);
            if (color.SelectedIndex < 0)
                color.SelectedIndex = 0;
            AutomationProperties.SetName(color, $"Color for tag {tag.Name}");
            AutomationProperties.SetAutomationId(color, $"tag-color-{tag.Id:N}");
            color.SelectionChanged += (_, _) =>
            {
                if (color.SelectedItem is ComboBoxItem { Tag: string selected })
                {
                    tag.Color = selected;
                    SaveManagedTagChanges();
                    RefreshBoard();
                }
            };
            Grid.SetColumn(color, 1);
            row.Children.Add(color);
            Button remove = IconButton($"Delete tag {tag.Name}", "");
            remove.Click += (_, _) => ShowDeletePrompt(tag);
            Grid.SetColumn(remove, 2);
            AutomationProperties.SetAutomationId(remove, $"delete-tag-{tag.Id:N}");
            row.Children.Add(remove);
            tagRows[tag.Id] = row;
            body.Children.Add(row);
        }

        void AddManagedTag()
        {
            if (pendingTag is not null)
            {
                if (SaveManagedTagChanges())
                    SetAddStatus(string.Empty);
                else
                    SetAddStatus("Could not save the tag. It is retained; select Retry after storage is available.");
                return;
            }

            string value = TaskDraftParser.Normalize(newTagName.Text);
            if (value.Length == 0)
            {
                SetAddStatus("Enter a tag name.");
                return;
            }
            if (value.Length > 80)
            {
                SetAddStatus("Tag names are limited to 80 characters.");
                return;
            }
            if (_document.Tags.Any(tag => string.Equals(TaskDraftParser.Normalize(tag.Name), value, StringComparison.OrdinalIgnoreCase)))
            {
                SetAddStatus("A tag with this name already exists.");
                return;
            }

            BoardTag tag = new() { Name = value, Color = ColorForIndex(_document.Tags.Count) };
            _document.Tags.Add(tag);
            AddTagRow(tag);
            newTagName.Text = string.Empty;
            RefreshBoard();
            if (SaveManagedTagChanges())
            {
                SetAddStatus(string.Empty);
                return;
            }

            SetPendingTag(tag);
            SetAddStatus("Could not save the tag. It is retained; select Retry after storage is available.");
        }

        addTag.Click += (_, _) => AddManagedTag();
        newTagName.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
                return;
            args.Handled = true;
            AddManagedTag();
        };
        foreach (BoardTag tag in _document.Tags.ToArray())
            AddTagRow(tag);
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _ = dialog.ShowAsync();
    }

    private void ShowTrash()
    {
        var dialog = new ContentDialog { Title = "Trash", CloseButtonText = "Done", XamlRoot = Root.XamlRoot };
        AutomationProperties.SetAutomationId(dialog, "trash-dialog");
        StackPanel body = new() { Spacing = 8 };
        List<BoardTask> deletedTasks = _document.Tasks.Where(item => item.DeletedUtc.HasValue).OrderByDescending(item => item.DeletedUtc).ToList();
        Button? emptyTrashButton = null;
        if (deletedTasks.Count > 0)
        {
            bool confirmEmptyTrash = false;
            TextBlock emptyTrashLabel = new() { Text = "Empty trash", VerticalAlignment = VerticalAlignment.Center };
            Button emptyTrash = new()
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 7,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                        emptyTrashLabel
                    }
                },
                Foreground = ErrorBrush(),
                Background = TransparentBrush(),
                BorderBrush = ErrorBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 38,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 0, 4)
            };
            emptyTrashButton = emptyTrash;
            AutomationProperties.SetName(emptyTrash, "Empty trash");
            AutomationProperties.SetAutomationId(emptyTrash, "empty-trash");
            emptyTrash.Click += (_, _) =>
            {
                int trashCount = _document.Tasks.Count(item => item.DeletedUtc.HasValue);
                if (trashCount == 0)
                {
                    emptyTrash.Visibility = Visibility.Collapsed;
                    return;
                }

                if (!confirmEmptyTrash)
                {
                    confirmEmptyTrash = true;
                    emptyTrashLabel.Text = "Click again to empty trash";
                    emptyTrash.Background = ErrorBrush();
                    emptyTrash.Foreground = new SolidColorBrush(Colors.White);
                    AutomationProperties.SetName(emptyTrash, "Click again to empty trash");
                    return;
                }

                TaskBoardOperations.EmptyTrash(_document);
                PersistTask();
                dialog.Hide();
                RefreshBoard();
            };
            body.Children.Add(emptyTrash);
        }

        foreach (BoardTask task in deletedTasks)
        {
            Grid row = new() { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            DateTimeOffset deadline = task.DeletedUtc!.Value.AddDays(30);
            row.Children.Add(new TextBlock { Text = $"{task.Title}\nDeletes {deadline.ToLocalTime():g}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            Button restore = new Button { Content = "Restore", Padding = new Thickness(9, 5, 9, 5) };
            AutomationProperties.SetName(restore, "Restore task from trash");
            AutomationProperties.SetAutomationId(restore, $"restore-trash-{task.Id:N}");
            restore.Click += (_, _) =>
            {
                TaskBoardOperations.RestoreTrash(task, DateTimeOffset.UtcNow);
                PersistTask();
                body.Children.Remove(row);
                if (!_document.Tasks.Any(item => item.DeletedUtc.HasValue) && emptyTrashButton is not null)
                    emptyTrashButton.Visibility = Visibility.Collapsed;
                RefreshBoard();
            };
            Grid.SetColumn(restore, 1);
            row.Children.Add(restore);
            body.Children.Add(row);
        }
        if (body.Children.Count == 0)
        {
            TextBlock empty = new() { Text = "Your trash is empty.", Foreground = SecondaryBrush() };
            AutomationProperties.SetName(empty, "Your trash is empty");
            AutomationProperties.SetAutomationId(empty, "trash-empty");
            body.Children.Add(empty);
        }
        ScrollViewer content = new() { Content = body, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.Content = content;
        _ = dialog.ShowAsync();
    }

    private bool CanAcceptTaskDrop(DragEventArgs args, BoardTaskStatus status)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            return false;
        if (!TryGetDraggedTaskId(out Guid id)
            || _document.Tasks.FirstOrDefault(task => task.Id == id) is not { DeletedUtc: null, ArchivedUtc: null } task)
            return false;
        return task.Status != status || View.Sort == BoardTaskSort.Manual;
    }

    private bool CanAcceptTagDrop(DragEventArgs args, BoardTask task)
    {
        if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
            || !TryGetDraggedTagId(out Guid tagId))
            return false;
        return !task.DeletedUtc.HasValue && !task.ArchivedUtc.HasValue && !task.TagIds.Contains(tagId);
    }

    private bool TryGetDraggedTaskId(out Guid id)
    {
        id = _draggingTask ?? Guid.Empty;
        return id != Guid.Empty;
    }

    private bool TryGetDraggedTagId(out Guid id)
    {
        id = _draggingTag ?? Guid.Empty;
        return id != Guid.Empty;
    }

    private void HandleDropAsync(BoardTaskStatus status, Guid? beforeId, DragEventArgs args)
    {
        if (TryGetDraggedTagId(out Guid tagId))
        {
            BoardTask? target = beforeId is Guid targetId
                ? _document.Tasks.FirstOrDefault(task => task.Id == targetId)
                : null;
            if (target is null || !CanAcceptTagDrop(args, target))
            {
                ClearDropColumn();
                return;
            }

            TaskBoardOperations.AssignTag(target, tagId, DateTimeOffset.UtcNow);
            PersistTask();
            RefreshBoard();
            _draggingTag = null;
            ClearDropColumn();
            return;
        }

        if (!CanAcceptTaskDrop(args, status) || !TryGetDraggedTaskId(out Guid sourceId))
        {
            ClearDropColumn();
            return;
        }
        BoardTask? source = _document.Tasks.FirstOrDefault(task => task.Id == sourceId);
        if (source is null || beforeId == sourceId)
        {
            ClearDropColumn();
            return;
        }

        if (source.Status != status)
            TaskBoardOperations.Move(source, status, DateTimeOffset.UtcNow);
        if (View.Sort == BoardTaskSort.Manual)
        {
            List<BoardTask> siblings = _document.Tasks
                .Where(task => !TaskBoardOperations.IsDeleted(task) && !task.ArchivedUtc.HasValue && task.Status == status && task.Id != source.Id)
                .OrderBy(task => task.ManualOrder)
                .ThenBy(task => task.CreatedUtc)
                .ToList();
            int insertAt = beforeId is Guid targetId
                ? siblings.FindIndex(task => task.Id == targetId)
                : -1;
            if (insertAt < 0)
                insertAt = siblings.Count;
            siblings.Insert(insertAt, source);
            for (int index = 0; index < siblings.Count; index++)
                siblings[index].ManualOrder = (index + 1) * 10L;
        }
        source.UpdatedUtc = DateTimeOffset.UtcNow;
        PersistTask();
        RefreshBoard();
        _draggingTask = null;
        ClearDropColumn();
    }

    private void HighlightDropColumn(BoardTaskStatus status)
    {
        foreach ((BoardTaskStatus columnStatus, Border surface) in _columnSurfaces)
            surface.BorderBrush = columnStatus == status ? AccentBrush() : TransparentBrush();
    }

    private void ClearDropColumn()
    {
        foreach (Border surface in _columnSurfaces.Values)
            surface.BorderBrush = TransparentBrush();
    }

    private bool PersistTask()
    {
        View.DrawerWidth = _drawerWidth;
        return TrySave();
    }

    private static void TouchTask(BoardTask task) => task.UpdatedUtc = DateTimeOffset.UtcNow;

    private Task<TaskBoardSaveResult>? _pendingWrite;

    private Task<TaskBoardSaveResult> BeginWrite()
    {
        _saveTimer.Stop();
        _saveScheduled = false;
        TaskBoardDocument snapshot = _document.Copy();
        var descriptions = new Dictionary<Guid, string>(_descriptionEdits);
        var titles = new Dictionary<Guid, string>(_titleEdits);
        string? quickEntry = _quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText) ? _quickEntryText : null;
        bool allowCleanup = _recoveryAcknowledged || _store.LoadWarning is null;
        return _pendingWrite = Task.Run(() =>
            _store.SaveWithRecovery(snapshot, descriptions, titles, quickEntry, allowCleanup));
    }

    private async Task SaveScheduledAsync()
    {
        if (_pendingWrite is not null)
        {
            _saveTimer.Start();
            return;
        }
        Task<TaskBoardSaveResult> pending = BeginWrite();
        TaskBoardSaveResult result = await pending.ConfigureAwait(false);
        // A synchronous flush may already have consumed this result.
        _dispatcher.TryEnqueue(() =>
        {
            if (_pendingWrite == pending)
            {
                _pendingWrite = null;
                ApplyWriteResult(result);
            }
        });
    }

    private void DrainWrite()
    {
        if (_pendingWrite is not { } pending)
            return;
        TaskBoardSaveResult result = pending.GetAwaiter().GetResult();
        _pendingWrite = null;
        ApplyWriteResult(result);
    }

    private bool TrySave()
    {
        DrainWrite();
        TaskBoardSaveResult result = BeginWrite().GetAwaiter().GetResult();
        _pendingWrite = null;
        ApplyWriteResult(result);
        return result.PrimarySaved;
    }

    private void ApplyWriteResult(TaskBoardSaveResult result)
    {
        if (result.PrimarySaved)
        {
            _document.Revision = Math.Max(_document.Revision, result.Snapshot.Revision);
            var saved = result.Snapshot.Tasks.ToDictionary(task => task.Id);
            foreach ((Guid id, string text) in _descriptionEdits.ToArray())
                if (saved.TryGetValue(id, out BoardTask? task) && task.Description == text)
                    _descriptionEdits.Remove(id);
            foreach ((Guid id, string text) in _titleEdits.ToArray())
                if (saved.TryGetValue(id, out BoardTask? task) && task.Title == text)
                    _titleEdits.Remove(id);
        }
        _writeFailed = !result.PrimarySaved || result.Error is not null;
        bool pending = _saveScheduled || _descriptionEdits.Count > 0 || _titleEdits.Count > 0
            || (_quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText));
        string status = !result.PrimarySaved
            ? result.RecoverySaved ? "Not saved · recovery copy available" : "Not saved · retry"
            : result.Error is not null ? "Saved · recovery update pending"
            : pending ? "Pending edits" : "Saved";
        if (result.PrimarySaved)
        {
            if (_titleEdits.Values.Any(string.IsNullOrWhiteSpace))
                status = "Title is required";
            else if (_titleEdits.Values.Any(title => title.Length > 400))
                status = "Task title is too long";
            else if (_descriptionEdits.Values.Any(description => description.Length > 100000))
                status = "Description is too long";
        }
        SetSaveStatus(status, _writeFailed || pending);
        if (result.Error is not null)
            System.Diagnostics.Trace.WriteLine($"WinWispr task persistence failed: {result.Error}");
    }

    private bool TryWriteRecoveryCopy()
    {
        DrainWrite();
        try
        {
            if (!_store.CanSave || _store.RecoveryNeedsChoice)
                return false;
            _store.SaveRecovery(_document, _descriptionEdits, _titleEdits,
                _quickEntryActive && !string.IsNullOrWhiteSpace(_quickEntryText) ? _quickEntryText : null);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"WinWispr task recovery save failed: {exception.GetType().Name}");
            return false;
        }
    }

    private void SetSaveStatus(string text, bool failed)
    {
        _lastSaveText = text;
        _lastSaveFailed = failed;
        if (_saveStatus is not null)
        {
            _saveStatus.Text = text;
            _saveStatus.Foreground = failed ? ErrorBrush() : SecondaryBrush();
        }
        if (_footerSaveStatus is not null)
        {
            _footerSaveStatus.Text = text;
            _footerSaveStatus.Foreground = failed ? ErrorBrush() : SecondaryBrush();
        }
        if (_retrySaveButton is not null)
            _retrySaveButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleSave()
    {
        _saveScheduled = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        CommitDescriptionEditor();
        DrainWrite();
        if (_saveScheduled || _saveTimer.IsRunning || _writeFailed || HasPendingDurableData)
            TrySave();
    }

    private void CommitDescriptionEditor()
    {
        if (!_editingDescription || _descriptionEditor is not { } editor || _selectedTask is not Guid id
            || _document.Tasks.FirstOrDefault(task => task.Id == id) is not BoardTask task
            || string.Equals(task.Description, editor.Text, StringComparison.Ordinal))
            return;
        ApplyDescriptionEdit(task, editor.Text);
    }

    private void ApplyDescriptionEdit(BoardTask task, string text)
    {
        _descriptionEdits[task.Id] = text;
        ScheduleSave();
        if (text.Length <= 100000)
        {
            task.Description = text;
            TouchTask(task);
        }
        else
            SetSaveStatus("Description is too long", failed: true);
    }

    private void UpdateResponsiveLayout()
    {
        if (_boardGrid is null || Root.ActualWidth <= 0)
            return;
        bool narrow = Root.ActualWidth < 760;
        if (narrow == _narrow && !_narrow)
            return;
        _narrow = narrow;
        UpdateToolbarLayout();
        _narrowTabs.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        if (!_narrow)
        {
            _drawerResizeGrip.Visibility = _drawer.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            _drawer.Width = _drawerWidth;
            UpdateDrawerResizeGrip();
            UpdateNarrowTabVisuals();
            _boardGrid.ColumnDefinitions.Clear();
            foreach (BoardTaskStatus status in Enum.GetValues<BoardTaskStatus>())
            {
                _boardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (_boardGrid.Children.ElementAtOrDefault((int)status) is FrameworkElement child)
                {
                    child.Visibility = Visibility.Visible;
                    Grid.SetColumn(child, (int)status);
                }
            }
            return;
        }

        _boardGrid.ColumnDefinitions.Clear();
        _boardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (FrameworkElement child in _boardGrid.Children.OfType<FrameworkElement>())
        {
            BoardTaskStatus status = (BoardTaskStatus)_boardGrid.Children.IndexOf(child);
            child.Visibility = status == _activeNarrowStatus ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(child, 0);
        }
        _drawerResizeGrip.Visibility = Visibility.Collapsed;
        if (_drawer.Visibility == Visibility.Visible)
            _drawer.Width = Math.Max(0, _content.ActualWidth);
        UpdateNarrowTabVisuals();
    }

    private void UpdateToolbarLayout()
    {
        _toolbar.RowDefinitions.Clear();
        _toolbar.ColumnDefinitions.Clear();
        if (_narrow)
        {
            _toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_searchBox, 0);
            Grid.SetRow(_searchBox, 0);
            Grid.SetColumn(_toolbarActions, 0);
            Grid.SetRow(_toolbarActions, 1);
            _toolbarActions.Orientation = Orientation.Vertical;
            _toolbarActions.Spacing = 9;
            _toolbarActions.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sortBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            _createActions.HorizontalAlignment = HorizontalAlignment.Right;
            return;
        }

        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_searchBox, 0);
        Grid.SetRow(_searchBox, 0);
        Grid.SetColumn(_toolbarActions, 1);
        Grid.SetRow(_toolbarActions, 0);
        _toolbarActions.Orientation = Orientation.Horizontal;
        _toolbarActions.Spacing = 9;
        _toolbarActions.HorizontalAlignment = HorizontalAlignment.Right;
        _sortBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        _createActions.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void UpdateNarrowTabVisuals()
    {
        if (_narrowTabs is null)
            return;
        foreach (Button tab in _narrowTabs.Children.OfType<Button>())
        {
            bool selected = tab.Tag is BoardTaskStatus status && status == _activeNarrowStatus;
            tab.Background = selected ? AccentTintBrush() : TransparentBrush();
            tab.BorderBrush = selected ? AccentBrush() : DividerBrush();
            tab.BorderThickness = new Thickness(selected ? 1 : 0);
        }
    }

    private void UpdateSelectedOutline(Guid id)
    {
        foreach (ListView list in _lists.Values)
            foreach (Button card in VisualDescendants<Button>(list).Where(button => button.Tag is Guid))
            {
                bool selected = card.Tag is Guid cardId && cardId == id;
                card.BorderBrush = selected ? AccentBrush() : DividerBrush();
                if (selected)
                {
                    card.Resources["ButtonBorderBrushPointerOver"] = AccentBrush();
                    card.Resources["ButtonBorderBrushPressed"] = AccentBrush();
                }
                else
                {
                    card.Resources.Remove("ButtonBorderBrushPointerOver");
                    card.Resources.Remove("ButtonBorderBrushPressed");
                }
            }
    }

    private Button TagFilterButton(string text, Guid? id, string color = "")
    {
        Button button = new() { Content = text, Padding = new Thickness(9, 4, 9, 4), Background = id == View.TagFilter ? AccentTintBrush() : TagBrush(color), BorderBrush = id == View.TagFilter ? AccentBrush() : TransparentBrush(), BorderThickness = new Thickness(id == View.TagFilter ? 1 : 0), FontSize = 12 };
        AutomationProperties.SetName(button, id is Guid ? $"Filter by tag {text}" : "Show all tasks");
        if (id is Guid tagId)
        {
            ToolTipService.SetToolTip(button, $"Filter by tag {text}. Drag to assign.");
            AutomationProperties.SetAutomationId(button, $"tag-filter-{tagId:N}");
            EnableTagDrag(button, tagId);
        }
        else
        {
            AutomationProperties.SetAutomationId(button, "tag-filter-all");
        }
        button.Click += (_, _) => { View.TagFilter = View.TagFilter == id ? null : id; TrySave(); RefreshBoard(); };
        return button;
    }

    private void EnableTagDrag(Button button, Guid tagId)
    {
        button.CanDrag = true;
        Windows.Foundation.Point? dragOrigin = null;
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            DependencyObject? source = args.OriginalSource as DependencyObject;
            while (source is not null && source is not Button)
                source = VisualTreeHelper.GetParent(source);
            var point = args.GetCurrentPoint(button);
            dragOrigin = source == button && point.Properties.IsLeftButtonPressed ? point.Position : null;
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, args) =>
        {
            var point = args.GetCurrentPoint(button);
            if (dragOrigin is not { } origin || !point.Properties.IsLeftButtonPressed || _draggingTag.HasValue
                || Math.Max(Math.Abs(point.Position.X - origin.X), Math.Abs(point.Position.Y - origin.Y)) < 8)
                return;
            dragOrigin = null;
            _draggingTag = tagId;
            button.ReleasePointerCaptures();
            try { await button.StartDragAsync(point); }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                SetSaveStatus("Could not start dragging · use task details", failed: true);
                System.Diagnostics.Trace.WriteLine($"WinWispr tag drag failed: {exception}");
            }
            finally { _draggingTag = null; }
        }), true);
        button.PointerCaptureLost += (_, _) => dragOrigin = null;
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => dragOrigin = null), true);
        button.DragStarting += (_, args) =>
        {
            _draggingTag = tagId;
            args.Data.SetText(tagId.ToString("D"));
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        button.DropCompleted += (_, _) => { dragOrigin = null; _draggingTag = null; ClearDropColumn(); };
    }

    private Button TagChip(string text, Guid id, string color, bool removable = false)
    {
        Button button = new() { Content = removable ? $"{text}  ×" : text, Padding = new Thickness(7, 3, 7, 3), Background = TagBrush(color), BorderThickness = new Thickness(0), FontSize = 11.5 };
        AutomationProperties.SetName(button, removable ? $"Remove task tag {text}" : $"Filter by tag {text}");
        AutomationProperties.SetAutomationId(button, removable ? $"remove-task-tag-{id:N}" : $"task-tag-{id:N}");
        if (!removable)
            button.Click += (_, _) => { View.TagFilter = View.TagFilter == id ? null : id; TrySave(); RefreshBoard(); };
        return button;
    }

    private static Button IconButton(string name, string glyph)
    {
        Button button = new() { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(4), MinWidth = 30, MinHeight = 30, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }
    private static Button PrimaryButton(string text, string glyph) => new() { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } } }, Padding = new Thickness(12, 5, 12, 5), MinHeight = 34, Background = AccentBrush(), Foreground = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(0) };
    private static Button PrimaryIconButton(string name, string glyph)
    {
        Button button = new() { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(10, 5, 10, 5), MinHeight = 34, Background = AccentBrush(), Foreground = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }
    private static Button LinkButton(string text) => new() { Content = text, Padding = new Thickness(0, 4, 7, 4), Background = new SolidColorBrush(Colors.Transparent), Foreground = AccentBrush(), BorderThickness = new Thickness(0), FontSize = 11.5 };
    private static TextBlock MetadataLabel(string text) => new() { Text = text, Foreground = SecondaryBrush(), FontSize = 12, Margin = new Thickness(0, 14, 0, 6) };

    private static FrameworkElement RenderMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TextBlock { Text = "Add a description", Foreground = AccentBrush(), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

        var panel = new StackPanel { Spacing = 5 };
        bool code = false;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim() == "```")
            {
                code = !code;
                continue;
            }
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 };
            if (code)
            {
                block.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
                block.Text = line;
            }
            else
            {
                string content = line.TrimStart();
                if (Regex.IsMatch(content, @"^#{1,6}\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    int level = content.TakeWhile(character => character == '#').Count();
                    content = content[level..].TrimStart();
                    block.FontSize = level == 1 ? 20 : level == 2 ? 17 : 15;
                    block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                else if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal)
                    || Regex.IsMatch(content, @"^\d+[.]\s", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    int markerLength = content[0] is '-' or '*' ? 2 : content.IndexOf('.') + 2;
                    content = "• " + content[markerLength..];
                    block.Margin = new Thickness(10, 0, 0, 0);
                }
                AppendMarkdownInlines(block, content);
            }
            panel.Children.Add(block);
        }
        return panel;
    }

    private static void AppendMarkdownInlines(TextBlock block, string text)
    {
        const string pattern = @"(\[[^\]]+\]\((?:https?|mailto):[^)\s]+\)|\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)";
        int position = 0;
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (match.Index > position)
                block.Inlines.Add(new Run { Text = text[position..match.Index] });
            string token = match.Value;
            if (token.StartsWith("[", StringComparison.Ordinal)
                && token.IndexOf("](", StringComparison.Ordinal) is int split && split > 1
                && token.EndsWith(")", StringComparison.Ordinal))
            {
                string label = token[1..split];
                string uri = token[(split + 2)..^1];
                if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri)
                    && parsedUri.Scheme is "http" or "https" or "mailto")
                {
                    var link = new Hyperlink { NavigateUri = parsedUri };
                    link.Inlines.Add(new Run { Text = label });
                    link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(link.NavigateUri);
                    block.Inlines.Add(link);
                }
                else
                    block.Inlines.Add(new Run { Text = token });
            }
            else if (token.StartsWith("**", StringComparison.Ordinal))
                block.Inlines.Add(new Bold { Inlines = { new Run { Text = token[2..^2] } } });
            else if (token.StartsWith("*", StringComparison.Ordinal))
                block.Inlines.Add(new Italic { Inlines = { new Run { Text = token[1..^1] } } });
            else
                block.Inlines.Add(new Run { Text = token[1..^1] });
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            block.Inlines.Add(new Run { Text = text[position..] });
        if (block.Inlines.Count == 0)
            block.Inlines.Add(new Run { Text = text });
    }

    private void ToggleTopMost(Button button)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
        button.Content = new FontIcon { Glyph = presenter.IsAlwaysOnTop ? "" : "", FontSize = 14 };
        AutomationProperties.SetName(button, presenter.IsAlwaysOnTop ? "Unpin board from top" : "Pin board on top");
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        InstallMinimumSizeHook();
        HousekeepIfSafe();
        if (_placementApplied)
            return;
        WindowPlacement placement = _savedPlacement is WindowPlacement saved && saved.IsUsable
            ? saved.ConstrainTo(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.X, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Y, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Width, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Height, MinimumWidth, MinimumHeight)
            : WindowPlacement.Centered(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.X, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Y, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Width, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Height, DefaultWidth, DefaultHeight);
        AppWindow.Resize(new SizeInt32(placement.Width, placement.Height));
        AppWindow.Move(new PointInt32(placement.X, placement.Y));
        _placementApplied = true;
    }

    private void HousekeepIfSafe()
    {
        if (_store.IsRecovered || _store.RecoveryNeedsChoice || !_recoveryAcknowledged)
            return;
        if (TaskBoardOperations.Housekeep(_document, DateTimeOffset.UtcNow) > 0)
        {
            if (TrySave())
            {
                try { _store.RefreshBackup(); }
                catch (IOException exception) { SetSaveStatus("Saved · backup refresh pending", failed: true); System.Diagnostics.Trace.WriteLine($"WinWispr task backup refresh failed: {exception.GetType().Name}"); }
                catch (UnauthorizedAccessException) { SetSaveStatus("Saved · backup refresh pending", failed: true); }
            }
            RefreshBoard();
        }
    }

    private void OnClosing(object sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        HideBoard();
    }

    private void InstallMinimumSizeHook()
    {
        if (_minimumSizeHookInstalled)
            return;
        _windowHandle = WindowNative.GetWindowHandle(this);
        _originalWindowProc = SetWindowLongPtr(_windowHandle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        _minimumSizeHookInstalled = _originalWindowProc != 0;
    }

    private void RestoreMinimumSizeHook()
    {
        if (!_minimumSizeHookInstalled || _windowHandle == 0)
            return;
        SetWindowLongPtr(_windowHandle, GwlWndProc, _originalWindowProc);
        _minimumSizeHookInstalled = false;
        _originalWindowProc = 0;
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        nint result = CallWindowProc(_originalWindowProc, handle, message, wParam, lParam);
        if (message == WmGetMinMaxInfo && lParam != 0)
        {
            Marshal.WriteInt32(lParam, MinTrackSizeWidthOffset, DipToPixels(MinimumWidth, GetDpiForWindow(handle)));
            Marshal.WriteInt32(lParam, MinTrackSizeHeightOffset, DipToPixels(MinimumHeight, GetDpiForWindow(handle)));
        }
        return result;
    }

    private static int SortIndex(BoardTaskSort sort) => sort switch
    {
        BoardTaskSort.TitleAscending => 1,
        BoardTaskSort.TitleDescending => 2,
        BoardTaskSort.DueAscending => 3,
        BoardTaskSort.DueDescending => 4,
        BoardTaskSort.CreatedDescending => 5,
        BoardTaskSort.CreatedAscending => 6,
        _ => 0
    };

    private static BoardTaskSort SortAt(int index) => index switch
    {
        1 => BoardTaskSort.TitleAscending,
        2 => BoardTaskSort.TitleDescending,
        3 => BoardTaskSort.DueAscending,
        4 => BoardTaskSort.DueDescending,
        5 => BoardTaskSort.CreatedDescending,
        6 => BoardTaskSort.CreatedAscending,
        _ => BoardTaskSort.Manual
    };

    private static string StatusName(BoardTaskStatus status) => status switch
    {
        BoardTaskStatus.ToDo => "To Do",
        BoardTaskStatus.Doing => "Doing",
        BoardTaskStatus.Done => "Done",
        _ => status.ToString()
    };

    private static string FormatDue(DateOnly due)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (due == today)
            return "Today";
        if (due == today.AddDays(1))
            return "Tomorrow";
        return due.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string ColorForIndex(int index) => new[] { "green", "amber", "blue", "purple", "rose" }[index % 5];

    private static SolidColorBrush PrimaryBrush() => Brush("TextFillColorPrimaryBrush");
    private static SolidColorBrush SecondaryBrush() => Brush("TextFillColorSecondaryBrush");
    private static SolidColorBrush SurfaceBrush() => Brush("CardBackgroundFillColorDefaultBrush");
    private static SolidColorBrush DrawerSurfaceBrush() => Brush("SolidBackgroundFillColorBaseBrush");
    private static SolidColorBrush DividerBrush() => Brush("DividerStrokeColorDefaultBrush");
    private static SolidColorBrush TransparentBrush() => new(Colors.Transparent);
    private static SolidColorBrush AccentBrush() => Brush("AccentFillColorDefaultBrush");
    private static SolidColorBrush AccentTintBrush() => Brush("AccentFillColorSecondaryBrush");
    private static SolidColorBrush ErrorBrush() => Brush("SystemFillColorCriticalBrush");
    private static SolidColorBrush StatusBrush(BoardTaskStatus status) => status switch
    {
        BoardTaskStatus.Doing => AccentBrush(),
        BoardTaskStatus.Done => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 69, 130, 91)),
        _ => SecondaryBrush()
    };
    private static SolidColorBrush TagBrush(string color) => color switch
    {
        "green" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 0, 162, 64)),
        "amber" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 226, 150, 20)),
        "purple" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 146, 79, 247)),
        "rose" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 210, 80, 110)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 51, 156, 255))
    };
    private static SolidColorBrush Brush(string key) => Application.Current.Resources.TryGetValue(key, out object value) && value is SolidColorBrush brush ? brush : new SolidColorBrush(Colors.Transparent);
    private static int DipToPixels(double dip, uint dpi) => (int)Math.Round(dip * (dpi == 0 ? 96u : dpi) / 96d);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previousProcedure, nint handle, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
}
