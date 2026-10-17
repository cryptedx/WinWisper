using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WinWispr.Core;
using WinWispr.Diagnostics;

namespace WinWispr.Interop;

// Begin and End are serialized by an internal gate: the dictation session and the shutdown path may
// call them concurrently. No state survives an app restart.
internal sealed class TeamsMicrophone(Func<TeamsMicrophone.Target?>? findMeeting = null,
    Action<TechnicalEvent, double>? timing = null)
{
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TargetCacheLifetime = TimeSpan.FromSeconds(5);

    internal sealed record Target(Func<bool?> ReadMuted, Action Toggle, nint WindowHandle = 0);
    private sealed record DiscoveryResult(Target? Target, Exception? Error);

    private readonly Func<Target?> _findMeeting = findMeeting ?? FindMeeting;
    private readonly bool _usesDefaultFinder = findMeeting is null;
    private readonly object _discoveryGate = new();
    private readonly SemaphoreSlim _ownership = new(1, 1);
    private Target? _ownedMute;
    private Target? _cachedTarget;
    private DateTimeOffset _cachedTargetExpires;
    private Task<DiscoveryResult>? _discoveryTask;

    internal async Task BeginAsync(CancellationToken cancellationToken)
    {
        await _ownership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MuteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ownership.Release();
        }
    }

    private async Task MuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ownedMute is not null)
            throw new MeetingMuteException("Teams microphone cleanup is still pending");
        try
        {
            (Target? target, bool fromCache) = await GetTargetAsync(cancellationToken).ConfigureAwait(false);
            if (target is null)
                return;
            bool? muted = target.ReadMuted();
            if (muted is null)
            {
                InvalidateCachedTarget(target);
                if (fromCache)
                    return;
                throw new MeetingMuteException("Teams microphone status unavailable");
            }
            if (muted.Value)
                return;
            cancellationToken.ThrowIfCancellationRequested();
            // ponytail: Teams exposes only Invoke; simultaneous manual changes can race this read/toggle.
            if (!Measure(TechnicalEvent.TeamsMuteConfirmation, () => ToggleAndWait(target, true)))
                throw new MeetingMuteException("Teams mute could not be confirmed; check Teams");
            _ownedMute = target;
        }
        catch (OperationCanceledException) { throw; }
        catch (MeetingMuteException) { throw; }
        catch
        {
            throw new MeetingMuteException("Teams microphone control unavailable; check Teams");
        }
    }

    private async Task<(Target? Target, bool FromCache)> GetTargetAsync(CancellationToken cancellationToken)
    {
        // Skip the no-Teams case before the bounded Task.Run; thread-pool startup must not block capture.
        if (_usesDefaultFinder && !HasCurrentSessionTeamsProcess())
            return (null, false);

        Task<DiscoveryResult> discovery;
        lock (_discoveryGate)
        {
            if (_cachedTarget is not null && _cachedTargetExpires > DateTimeOffset.UtcNow)
                return (_cachedTarget, true);

            discovery = _discoveryTask ??= DiscoverAsync();
        }

        DiscoveryResult result;
        try
        {
            result = await discovery.WaitAsync(DiscoveryBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // ponytail: fail closed when Teams state is unknown; slow UI Automation may warm
            // the cache for the next dictation, but must not allow unmuted capture now.
            throw new MeetingMuteException("Teams microphone discovery timed out; check Teams");
        }

        if (result.Error is not null)
            throw result.Error;
        return (result.Target, false);
    }

    private async Task<DiscoveryResult> DiscoverAsync()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            Target? target;
            try
            {
                target = await Task.Run(_findMeeting).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return new DiscoveryResult(null, exception);
            }

            if (target is not null)
            {
                lock (_discoveryGate)
                {
                    _cachedTarget = target;
                    _cachedTargetExpires = DateTimeOffset.UtcNow.Add(TargetCacheLifetime);
                }
            }

            return new DiscoveryResult(target, null);
        }
        finally
        {
            try { timing?.Invoke(TechnicalEvent.TeamsDiscovery, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
            catch { }
            lock (_discoveryGate)
                _discoveryTask = null;
        }
    }

    private void InvalidateCachedTarget(Target target)
    {
        lock (_discoveryGate)
        {
            if (ReferenceEquals(_cachedTarget, target))
            {
                _cachedTarget = null;
                _cachedTargetExpires = default;
            }
        }
    }

    private T Measure<T>(TechnicalEvent phase, Func<T> action)
    {
        long started = Stopwatch.GetTimestamp();
        try { return action(); }
        finally
        {
            // Diagnostics must never affect mute ownership or capture cleanup.
            try { timing?.Invoke(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
            catch { }
        }
    }

    /// <summary>Restores a mute this instance owns. Returns a warning when the restore was not confirmed.</summary>
    internal async Task<DictationNotice?> EndAsync()
    {
        await _ownership.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(RestoreOwnedMute).ConfigureAwait(false);
        }
        finally
        {
            _ownership.Release();
        }
    }

    private DictationNotice? RestoreOwnedMute()
    {
        Target? target = _ownedMute;
        _ownedMute = null;
        if (target is null)
            return null;
        try
        {
            bool? muted = target.ReadMuted();
            if (muted == false)
                return null;
            if (muted is null)
                throw new InvalidOperationException();
            if (!ToggleAndWait(target, false))
                throw new InvalidOperationException();
            return null;
        }
        catch
        {
            // Never retry an uncertain toggle or restore into a different meeting.
            return new DictationNotice(DictationNoticeKind.TeamsMicrophone,
                "Check Teams microphone; automatic restore was not confirmed");
        }
    }

    private static bool ToggleAndWait(Target target, bool muted)
    {
        nint previousWindow = target.WindowHandle == 0 ? 0 : GetForegroundWindow();
        try
        {
            target.Toggle();
            return WaitForState(target, muted);
        }
        finally
        {
            // Invoke activates Teams. Native activation restores the editor's existing caret;
            // UI Automation SetFocus can instead activate a child HWND in Notepad.
            if (previousWindow != 0 && ShouldRestoreFocus(previousWindow, GetForegroundWindow(), target.WindowHandle))
                SetForegroundWindow(previousWindow);
        }
    }

    internal static bool ShouldRestoreFocus(nint previous, nint current, nint teams) =>
        previous != 0 && teams != 0 && previous != teams && current == teams;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    private static bool WaitForState(Target target, bool expected)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            bool? muted = target.ReadMuted();
            if (muted == expected)
                return true;
            if (muted is null)
                return false;
            Thread.Sleep(50);
        }
        return false;
    }

    internal static bool? ParseMuted(string name) => name.Trim() switch
    {
        "Mikrofon stummschalten" or "Mute mic" or "Mute microphone" or "Mute" => false,
        "Mikrofon wieder aktivieren" or "Stummschaltung aufheben" or "Stummschaltung des Mikrofons aufheben" or
        "Mikrofonstummschaltung aufheben" or "Unmute mic" or "Unmute microphone" or "Unmute" => true,
        _ => null
    };

    internal static Target? FindMeeting()
    {
        var processIds = GetCurrentSessionTeamsProcessIds();
        if (processIds.Count == 0)
            return null;
        var meetings = new List<(AutomationElement Window, AutomationElement Hangup)>();
        // Filter native HWNDs before entering UI Automation. Unrelated applications
        // must not participate in the recording-start critical path.
        var handles = new List<nint>();
        if (!EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint processId);
            if (processIds.Contains(unchecked((int)processId)))
                handles.Add(handle);
            return true;
        }, 0))
            throw new MeetingMuteException("Teams windows could not be enumerated");
        foreach (nint handle in handles)
        {
            AutomationElement window = AutomationElement.FromHandle(handle);
            if (!processIds.Contains(window.Current.ProcessId))
                continue;
            if (FindButton(window, "hangup-button") is { } hangup)
                meetings.Add((window, hangup));
        }
        int selected = SelectMeetingWindow(meetings.Select(meeting =>
            (meeting.Window.Current.ProcessId, meeting.Window.Current.Name)).ToArray());
        if (selected < 0)
            return null;
        {
            var (window, hangup) = meetings[selected];
            AutomationElement microphone = FindButton(window, "microphone-button")
                ?? throw new MeetingMuteException("Teams meeting microphone is unavailable");
            int[] meetingId = hangup.GetRuntimeId();
            int[] microphoneId = microphone.GetRuntimeId();
            bool IsSameMeeting() =>
                FindButton(window, "hangup-button") is { } currentHangup
                && currentHangup.GetRuntimeId().SequenceEqual(meetingId)
                && FindButton(window, "microphone-button") is { } currentMic
                && currentMic.GetRuntimeId().SequenceEqual(microphoneId);
            return new Target(
                () => IsSameMeeting() && microphone.Current.IsEnabled
                    ? ParseMuted(microphone.Current.Name) : null,
                () =>
                {
                    if (!IsSameMeeting() || !microphone.Current.IsEnabled)
                        throw new InvalidOperationException("The Teams meeting changed.");
                    ((InvokePattern)microphone.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }, window.Current.NativeWindowHandle);
        }
    }

    private static bool HasCurrentSessionTeamsProcess() => GetCurrentSessionTeamsProcessIds().Count > 0;

    private static HashSet<int> GetCurrentSessionTeamsProcessIds()
    {
        using Process currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;
        var processIds = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName("ms-teams"))
        {
            using (process)
                if (process.SessionId == sessionId)
                    processIds.Add(process.Id);
        }
        return processIds;
    }

    internal static int SelectMeetingWindow(IReadOnlyList<(int ProcessId, string Title)> windows)
    {
        int selected = -1;
        for (int index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            string? mainTitle = null;
            foreach (string prefix in new[] { "Kompakte Besprechungsansicht | ", "Compact meeting view | " })
                if (window.Title.StartsWith(prefix, StringComparison.Ordinal))
                    mainTitle = window.Title[prefix.Length..];
            // Teams mirrors a call in a compact window when another app has focus.
            // Prefer the persistent main view only when its title and process match exactly.
            if (mainTitle is not null && windows.Any(other =>
                other.ProcessId == window.ProcessId && other.Title == mainTitle))
                continue;
            if (selected >= 0)
                throw new MeetingMuteException("Teams meeting microphone is ambiguous");
            selected = index;
        }
        return selected;
    }

    private static AutomationElement? FindButton(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
