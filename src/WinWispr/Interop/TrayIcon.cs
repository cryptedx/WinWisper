using System.Runtime.InteropServices;
using WinWispr.Audio;
using WinWispr.Core;

namespace WinWispr.Interop;

internal enum TrayCommand
{
    Settings = 1,
    StartStop = 2,
    Board = 3,
    Exit = 4,
    DueSummary = 5,
    TranslateClipboard = 6,

    /// <summary>Checks for an update, or installs the one already found.</summary>
    Update = 7
}

/// <summary>The commands of the tray menu below the microphone choice, in order, with their labels.</summary>
internal static class TrayMenuEntries
{
    internal static IReadOnlyList<(TrayCommand Command, string Label)> Items { get; } =
    [
        (TrayCommand.Settings, "Settings"),
        (TrayCommand.StartStop, "Start/Stop"),
        (TrayCommand.TranslateClipboard, "Translate clipboard"),
        (TrayCommand.Board, "Task board"),
        (TrayCommand.Update, "Check for updates"),
        (TrayCommand.Exit, "Exit")
    ];

    /// <summary>Whether a value returned by the popup menu is one of the menu's commands.</summary>
    internal static bool IsMenuCommand(uint command) => Items.Any(item => (uint)item.Command == command);
}



internal sealed class TrayIcon : IDisposable
{
    private const uint WmAppTray = 0x8004;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonUp = 0x0202;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint NinBalloonHide = 0x0403;
    private const uint NinBalloonTimeout = 0x0404;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifInfo = 0x10;
    private const uint MfString = 0;
    private const uint MfPopup = 0x0010;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;
    private const uint MfSeparator = 0x0800;
    private const uint MicrophoneCommandBase = 100;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private readonly NativeMessageWindow _messageWindow;
    private readonly Action<TrayCommand, WindowTarget> _dispatch;
    private readonly Func<Task<MicrophoneSelection>> _loadMicrophones;
    private readonly Action<string?> _selectMicrophone;
    private readonly uint _iconId = unchecked((uint)Environment.ProcessId);
    private readonly nint _iconHandle;
    private WindowTarget _target;
    private TrayCommand? _balloonCommand;
    private bool _disposed;
    private bool _showingMenu;

    internal TrayIcon(Action<TrayCommand, WindowTarget> dispatch,
        Func<Task<MicrophoneSelection>> loadMicrophones, Action<string?> selectMicrophone)
    {
        _dispatch = dispatch;
        _loadMicrophones = loadMicrophones;
        _selectMicrophone = selectMicrophone;
        _messageWindow = new NativeMessageWindow(OnMessage);
        _iconHandle = LoadImage(
            0,
            Path.Combine(AppContext.BaseDirectory, "Assets", "WinWisprIcon.ico"),
            ImageIcon,
            0,
            0,
            LrLoadFromFile | LrDefaultSize);
        if (_iconHandle == 0)
        {
            _messageWindow.Dispose();
            throw new InvalidOperationException("Could not load the notification-area icon.");
        }

        var data = CreateData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            DestroyIcon(_iconHandle);
            _messageWindow.Dispose();
            throw new InvalidOperationException("Could not create the notification-area icon.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var data = CreateData();
        Shell_NotifyIcon(NimDelete, ref data);
        DestroyIcon(_iconHandle);
        _messageWindow.Dispose();
    }

    /// <summary>The label of the update entry while an update is known, such as "Install update 1.11.0".</summary>
    internal string? UpdateMenuLabel { get; set; }

    internal bool ShowPasteFallback() => ShowNotice(
        "Automatic paste unavailable",
        "Your text is on the clipboard. Press Ctrl+V to paste it manually.",
        warning: true);

    internal bool ShowTaskSummary(TaskDailySummary summary) =>
        ShowNotice(summary.NotificationTitle, summary.NotificationBody, clickCommand: TrayCommand.DueSummary);

    internal bool ShowUpdateAvailable(string version) => ShowNotice(
        $"WinWispr {version} is available",
        "Click to update. WinWispr restarts when the update is installed.",
        clickCommand: TrayCommand.Update);

    /// <summary>Shows a balloon; clicking it dispatches <paramref name="clickCommand"/>, when one is given.</summary>
    internal bool ShowNotice(string title, string body, TrayCommand? clickCommand = null, bool warning = false)
    {
        if (_disposed)
            return false;
        if (clickCommand is not null)
            _target = new WindowTarget(GetForegroundWindow());
        var data = CreateData();
        data.Flags = NifInfo;
        data.InfoTitle = title;
        data.Info = body;
        data.InfoFlags = (warning ? 0x02u : 0u) | 0x10; // NIIF_WARNING, NIIF_NOSOUND
        bool shown = Shell_NotifyIcon(NimModify, ref data);
        // A new balloon replaces the previous one, and with it what a click on it meant.
        _balloonCommand = shown ? clickCommand : null;
        return shown;
    }

    private nint OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == WmAppTray && unchecked((uint)lParam.ToInt64()) == NinBalloonUserClick
            && _balloonCommand is { } balloonCommand)
        {
            _balloonCommand = null;
            _dispatch(balloonCommand, _target);
            return 1;
        }
        if (message == WmAppTray && unchecked((uint)lParam.ToInt64()) is NinBalloonHide or NinBalloonTimeout)
            _balloonCommand = null;
        if (message != WmAppTray || unchecked((uint)lParam.ToInt64()) is not (WmRButtonUp or WmLButtonUp))
            return 0;

        _target = new WindowTarget(GetForegroundWindow());
        _ = ShowMenuAsync();
        return 1;
    }

    private async Task ShowMenuAsync()
    {
        if (_showingMenu || _disposed)
            return;
        _showingMenu = true;
        try
        {
            MicrophoneSelection microphones = await _loadMicrophones();
            if (!_disposed)
                ShowMenu(microphones);
        }
        finally
        {
            _showingMenu = false;
        }
    }

    private void ShowMenu(MicrophoneSelection microphones)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
            return;

        nint microphoneMenu = CreatePopupMenu();
        try
        {
            if (microphoneMenu != 0)
            {
                AppendMenu(microphoneMenu, MfString | (microphones.EffectiveId is null ? MfChecked : 0),
                    MicrophoneCommandBase, "Windows default");
                for (int index = 0; index < microphones.Devices.Count; index++)
                {
                    Microphone device = microphones.Devices[index];
                    AppendMenu(microphoneMenu,
                        MfString | (device.Id == microphones.EffectiveId ? MfChecked : 0),
                        MicrophoneCommandBase + (uint)index + 1, device.Name);
                }
                if (microphones.State == MicrophoneLoadState.EnumerationFailed)
                    AppendMenu(microphoneMenu, MfString | MfGrayed, 0, "Could not list microphones");
                else if (microphones.Devices.Count == 0)
                    AppendMenu(microphoneMenu, MfString | MfGrayed, 0, "No microphones found");
                string selectedName = microphones.Devices
                    .FirstOrDefault(device => device.Id == microphones.EffectiveId)?.Name
                    ?? "Windows default";
                AppendMenu(menu, MfPopup, (nuint)microphoneMenu, $"Microphone: {selectedName}");
                microphoneMenu = 0; // The parent menu owns the submenu.
                AppendMenu(menu, MfSeparator, 0, string.Empty);
            }
            foreach ((TrayCommand entry, string label) in TrayMenuEntries.Items)
                AppendMenu(menu, MfString, (nuint)entry,
                    entry == TrayCommand.Update && UpdateMenuLabel is { } updateLabel ? updateLabel : label);
            GetCursorPos(out Point cursor);
            SetForegroundWindow(_messageWindow.Handle);
            uint command = TrackPopupMenu(
                menu,
                TpmReturnCommand | TpmNonotify,
                cursor.X,
                cursor.Y,
                0,
                _messageWindow.Handle,
                0);
            if (TrayMenuEntries.IsMenuCommand(command))
            {
                if (command == (uint)TrayCommand.StartStop && _target.Hwnd != 0)
                    SetForegroundWindow(_target.Hwnd);
                _dispatch((TrayCommand)command, _target);
            }
            else if (command == MicrophoneCommandBase)
                _selectMicrophone(null);
            else if (command > MicrophoneCommandBase
                && command - MicrophoneCommandBase <= microphones.Devices.Count)
                _selectMicrophone(microphones.Devices[(int)(command - MicrophoneCommandBase - 1)].Id);
        }
        finally
        {
            if (microphoneMenu != 0)
                DestroyMenu(microphoneMenu);
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _messageWindow.Handle,
        Id = _iconId,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmAppTray,
        IconHandle = _iconHandle,
        Tip = "WinWispr"
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint identifier, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint owner,
        nint rectangle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
