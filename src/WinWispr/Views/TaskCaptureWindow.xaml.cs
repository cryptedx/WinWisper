using System.Runtime.InteropServices;
using WinRT.Interop;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinWispr.Core;
using Windows.Graphics;

namespace WinWispr.Views;

internal sealed partial class TaskCaptureWindow : Window
{
    private readonly Guid _draftId;
    private readonly TaskDraftParseResult _parsed;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<string> _tagNames;
    private readonly List<string> _suggestedTagNames;
    private IReadOnlyList<BoardTag> _tags;
    private readonly Action<Guid, string, string, DateOnly?, IReadOnlyList<string>> _draftChanged;
    private readonly Func<Guid, bool> _discard;
    private bool _disposing;
    private bool _placementApplied;
    private StackPanel _tagStrip = null!;
    private StackPanel _availableTagsSection = null!;
    private StackPanel? _availableTagStrip;
    private StackPanel _suggestionStrip = null!;
    private AutoSuggestBox _tagInput = null!;
    private TextBox _title = null!;
    private TextBox _description = null!;
    private CalendarDatePicker _dueDate = null!;
    private TextBlock _saveStatus = null!;

    internal TaskCaptureWindow(
        Guid draftId,
        TaskDraftParseResult parsed,
        IReadOnlyList<BoardTag> tags,
        DispatcherQueue dispatcher,
        Func<Guid, TaskDraftParseResult, string, string, DateOnly?, IReadOnlyList<string>, bool> commit,
        Action<Guid, string, string, DateOnly?, IReadOnlyList<string>> draftChanged,
        Func<Guid, bool> discard)
    {
        _draftId = draftId;
        _parsed = parsed;
        _tags = tags;
        _dispatcher = dispatcher;
        _draftChanged = draftChanged;
        _discard = discard;
        _tagNames = parsed.SelectedTagIds
            .Select(id => tags.FirstOrDefault(tag => tag.Id == id)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Concat(parsed.ExplicitTagNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _suggestedTagNames = parsed.SuggestedTagIds
            .Select(id => tags.FirstOrDefault(tag => tag.Id == id)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Where(name => !_tagNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        InitializeComponent();
        BuildUi(commit);
        ExtendsContentIntoTitleBar = true;
        Activated += OnActivated;
        AppWindow.Closing += (_, args) =>
        {
            if (_disposing)
                return;
            args.Cancel = true;
            AppWindow.Hide();
        };
    }

    internal void ShowCapture()
    {
        Activate();
        AppWindow.Show();
    }

    internal Guid DraftId => _draftId;
    internal bool IsVisible => AppWindow.IsShownInSwitchers;

    internal void UpdateTags(IReadOnlyList<BoardTag> tags)
    {
        _tags = tags;
        _availableTagsSection.Visibility = _tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshTagInputItems();
        RefreshTags();
    }

    internal void Dispose()
    {
        _disposing = true;
        Close();
    }

    private void BuildUi(Func<Guid, TaskDraftParseResult, string, string, DateOnly?, IReadOnlyList<string>, bool> commit)
    {
        SystemBackdrop = new MicaBackdrop();
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid titleBar = new() { Padding = new Thickness(18, 0, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        SetTitleBar(titleBar);
        titleBar.Children.Add(new TextBlock { Text = "WinWispr", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        TextBlock titleText = new() { Text = "  New task", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = SecondaryBrush() };
        Grid.SetColumn(titleText, 1);
        titleBar.Children.Add(titleText);
        Grid.SetRow(titleBar, 0);
        Root.Children.Add(titleBar);

        ScrollViewer scroll = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            IsVerticalRailEnabled = true
        };
        StackPanel panel = new() { Padding = new Thickness(30, 22, 30, 24), Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "New task", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Review the captured text before it is added to To Do.", Foreground = SecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Label("Captured text"));
        var transcript = new TextBox { Text = _parsed.OriginalTranscript, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 150 };
        AutomationProperties.SetName(transcript, "Captured task text");
        AutomationProperties.SetAutomationId(transcript, "captured-task-text");
        panel.Children.Add(transcript);
        panel.Children.Add(Label("Title"));
        _title = new TextBox { Text = _parsed.SuggestedTitle, MinHeight = 38 };
        AutomationProperties.SetName(_title, "Task title");
        AutomationProperties.SetAutomationId(_title, "capture-task-title");
        _title.TextChanged += (_, _) => PersistDraft();
        panel.Children.Add(_title);
        panel.Children.Add(Label("Description (Markdown)"));
        // AcceptsReturn must precede Text: a single-line TextBox keeps only the first line it is given.
        _description = new TextBox { AcceptsReturn = true, Text = _parsed.Description, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
        AutomationProperties.SetName(_description, "Task description in Markdown");
        AutomationProperties.SetAutomationId(_description, "capture-task-description");
        _description.TextChanged += (_, _) => PersistDraft();
        panel.Children.Add(_description);
        panel.Children.Add(Label("Due date"));
        _dueDate = new CalendarDatePicker { Date = _parsed.SuggestedDueDate is DateOnly due ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue)) : null };
        AutomationProperties.SetName(_dueDate, "Task due date");
        AutomationProperties.SetAutomationId(_dueDate, "capture-task-due-date");
        _dueDate.DateChanged += (_, _) => PersistDraft();
        panel.Children.Add(_dueDate);
        if (_parsed.UnresolvedDatePhrase is string unresolved)
            panel.Children.Add(new TextBlock { Text = $"Date phrase needs review: {unresolved}", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 105, 0)), TextWrapping = TextWrapping.Wrap });

        panel.Children.Add(Label("Tags"));
        _tagStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(_tagStrip);
        _availableTagsSection = new StackPanel { Spacing = 6, Visibility = _tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible };
        _availableTagsSection.Children.Add(Label("Available tags"));
        StackPanel availableTags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        _availableTagStrip = availableTags;
        _availableTagsSection.Children.Add(new ScrollViewer
        {
            Content = availableTags,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled
        });
        panel.Children.Add(_availableTagsSection);
        _tagInput = new AutoSuggestBox { PlaceholderText = "Add a tag", ItemsSource = _tags.Select(tag => tag.Name).ToArray(), Width = 220 };
        AutomationProperties.SetName(_tagInput, "Capture task tags");
        AutomationProperties.SetAutomationId(_tagInput, "capture-task-tags");
        _tagInput.TextChanged += (_, _) => RefreshTagInputItems();
        _tagInput.QuerySubmitted += (_, args) =>
        {
            string name = TaskDraftParser.Normalize(args.QueryText);
            if (name.Length > 0 && !_tagNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                _tagNames.Add(name);
            _tagInput.Text = string.Empty;
            RefreshTags();
            PersistDraft();
        };
        panel.Children.Add(_tagInput);
        if (_suggestedTagNames.Count > 0)
        {
            panel.Children.Add(Label("Suggested tags"));
            _suggestionStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(_suggestionStrip);
        }
        RefreshTags();

        Grid actions = new() { Margin = new Thickness(0, 18, 0, 0), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 8 };
        Button cancel = new() { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7) };
        AutomationProperties.SetName(cancel, "Cancel task capture");
        AutomationProperties.SetAutomationId(cancel, "capture-cancel");
        cancel.Click += (_, _) => AppWindow.Hide();
        Button discard = new() { Content = "Discard draft", Padding = new Thickness(14, 7, 14, 7) };
        AutomationProperties.SetName(discard, "Discard task draft");
        AutomationProperties.SetAutomationId(discard, "capture-discard");
        discard.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Discard unfinished task?",
                Content = "The captured text and edits will be removed.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep editing",
                XamlRoot = Root.XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "discard-draft-dialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            if (_discard(_draftId))
                AppWindow.Hide();
            else
                _saveStatus.Text = "Could not discard the draft. Your edits remain here; retry when storage is available.";
        };
        _saveStatus = new TextBlock { Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 55, 55)), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_saveStatus);
        void SaveTask()
        {
            DateOnly? date = _dueDate.Date is DateTimeOffset selected ? DateOnly.FromDateTime(selected.DateTime) : null;
            if (commit(_draftId, _parsed, _title.Text, _description.Text, date, _tagNames.ToArray()))
            {
                _saveStatus.Text = string.Empty;
                AppWindow.Hide();
            }
            else
                _saveStatus.Text = "Could not save the task. Your edits remain here; check disk access and try again.";
        }
        _title.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                SaveTask();
            }
        };
        Button add = new() { Content = "Add task", Padding = new Thickness(15, 7, 15, 7), Background = AccentBrush(), Foreground = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(add, "Add captured task");
        AutomationProperties.SetAutomationId(add, "capture-add-task");
        add.Click += (_, _) => SaveTask();
        Grid.SetColumn(discard, 1);
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(add, 3);
        actions.Children.Add(discard);
        actions.Children.Add(cancel);
        actions.Children.Add(add);
        panel.Children.Add(actions);
        scroll.Content = panel;
        Grid.SetRow(scroll, 1);
        Root.Children.Add(scroll);
        _dispatcher.TryEnqueue(() => _title.Focus(FocusState.Programmatic));
    }

    private void RefreshTags()
    {
        _tagStrip.Children.Clear();
        foreach (string name in _tagNames.ToArray())
        {
            string color = _tags.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))?.Color ?? "blue";
            Button chip = new() { Content = name + "  ×", Padding = new Thickness(8, 3, 8, 3), Background = TagBrush(color), BorderThickness = new Thickness(0), FontSize = 12 };
            AutomationProperties.SetName(chip, $"Remove capture tag {name}");
            AutomationProperties.SetAutomationId(chip, $"capture-remove-tag-{name}");
            chip.Click += (_, _) => { _tagNames.Remove(name); RefreshTags(); PersistDraft(); };
            _tagStrip.Children.Add(chip);
        }
        if (_availableTagStrip is not null)
        {
            _availableTagStrip.Children.Clear();
            foreach (BoardTag tag in _tags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                bool selected = _tagNames.Contains(tag.Name, StringComparer.OrdinalIgnoreCase);
                Button choice = new()
                {
                    Content = tag.Name,
                    Padding = new Thickness(8, 3, 8, 3),
                    Background = selected ? TagBrush(tag.Color) : TransparentBrush(),
                    BorderBrush = selected ? AccentBrush() : DividerBrush(),
                    BorderThickness = new Thickness(1),
                    FontSize = 12
                };
                AutomationProperties.SetName(choice, $"{(selected ? "Remove" : "Add")} tag {tag.Name}");
                AutomationProperties.SetAutomationId(choice, $"capture-tag-{tag.Id:N}");
                choice.Click += (_, _) =>
                {
                    if (_tagNames.RemoveAll(name => string.Equals(name, tag.Name, StringComparison.OrdinalIgnoreCase)) == 0)
                        _tagNames.Add(tag.Name);
                    RefreshTags();
                    PersistDraft();
                };
                _availableTagStrip.Children.Add(choice);
            }
        }
        if (_suggestionStrip is null)
            return;
        _suggestionStrip.Children.Clear();
        foreach (string name in _suggestedTagNames.Where(name => !_tagNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            Button suggestion = new() { Content = $"+ {name}", Padding = new Thickness(8, 3, 8, 3), Background = TransparentBrush(), BorderBrush = DividerBrush(), BorderThickness = new Thickness(1), FontSize = 12 };
            AutomationProperties.SetName(suggestion, $"Suggest tag {name}");
            AutomationProperties.SetAutomationId(suggestion, $"capture-suggested-tag-{name}");
            suggestion.Click += (_, _) =>
            {
                _suggestedTagNames.Remove(name);
                if (!_tagNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    _tagNames.Add(name);
                RefreshTags();
                PersistDraft();
            };
            _suggestionStrip.Children.Add(suggestion);
        }
    }

    private void RefreshTagInputItems()
    {
        string query = TaskDraftParser.Normalize(_tagInput.Text);
        _tagInput.ItemsSource = _tags.Select(tag => tag.Name)
            .Where(name => query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
    }

    private void PersistDraft()
    {
        if (_title is null || _description is null || _dueDate is null)
            return;
        DateOnly? date = _dueDate.Date is DateTimeOffset selected ? DateOnly.FromDateTime(selected.DateTime) : null;
        _draftChanged(_draftId, _title.Text, _description.Text, date, _tagNames.ToArray());
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_placementApplied)
            return;
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        int width = (int)Math.Round(540 * scale);
        int height = Math.Min((int)Math.Round(700 * scale), area.WorkArea.Height - (int)Math.Round(80 * scale));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2), area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
        _placementApplied = true;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = SecondaryBrush(), Margin = new Thickness(0, 10, 0, 0) };
    private static SolidColorBrush PrimaryBrush() => Brush("TextFillColorPrimaryBrush");
    private static SolidColorBrush SecondaryBrush() => Brush("TextFillColorSecondaryBrush");
    private static SolidColorBrush AccentBrush() => Brush("AccentFillColorDefaultBrush");
    private static SolidColorBrush DividerBrush() => Brush("DividerStrokeColorDefaultBrush");
    private static SolidColorBrush TransparentBrush() => new(Colors.Transparent);
    private static SolidColorBrush Brush(string key) => Application.Current.Resources.TryGetValue(key, out object value) && value is SolidColorBrush brush ? brush : new SolidColorBrush(Colors.Transparent);
    private static SolidColorBrush TagBrush(string color)
    {
        return color switch
        {
            "green" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 0, 162, 64)),
            "amber" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 226, 150, 20)),
            "purple" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 146, 79, 247)),
            "rose" => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 210, 80, 110)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(55, 51, 156, 255))
        };
    }
}
