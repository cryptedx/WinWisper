using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class GlobalInputTests
{
    [TestMethod]
    public void Replay_hotkey_only_routes_while_idle()
    {
        foreach (RecordingState state in Enum.GetValues<RecordingState>())
            Assert.AreEqual(state == RecordingState.Idle ? GlobalInputAction.ReplayLast : GlobalInputAction.None,
                GlobalInputRouter.OnReplayHotkey(state).Action);
    }

    [TestMethod]
    public void Hotkeys_route_presses_without_deciding_start_or_stop()
    {
        Assert.AreEqual(GlobalInputAction.PressPrimary, GlobalInputRouter.OnRegisteredHotkey().Action);
        Assert.AreEqual(GlobalInputAction.PressTask, GlobalInputRouter.OnTaskHotkey().Action);
        Assert.AreEqual(GlobalInputAction.OpenBoard, GlobalInputRouter.OnBoardHotkey().Action);
        Assert.IsFalse(GlobalInputRouter.OnRegisteredHotkey().Suppress);
    }

    [TestMethod]
    public void Every_hotkey_key_up_is_a_release_of_the_hotkeys_using_that_key()
    {
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleasePrimary },
            GlobalInputRouter.OnHotkeyKeyUp(0x58, 0x58, 0x54).ToArray());
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleaseTask },
            GlobalInputRouter.OnHotkeyKeyUp(0x54, 0x58, 0x54).ToArray());
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleasePrimary, GlobalInputAction.ReleaseTask },
            GlobalInputRouter.OnHotkeyKeyUp(0x41, 0x41, 0x41).ToArray());
        Assert.AreEqual(0, GlobalInputRouter.OnHotkeyKeyUp(0x41, 0x58, null).Count());
    }

    [TestMethod]
    public void The_english_dictation_hotkey_routes_its_press_and_release_like_the_other_triggers()
    {
        Assert.AreEqual(GlobalInputAction.PressEnglishDictation, GlobalInputRouter.OnEnglishDictationHotkey().Action);
        Assert.IsFalse(GlobalInputRouter.OnEnglishDictationHotkey().Suppress);
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleaseEnglishDictation },
            GlobalInputRouter.OnHotkeyKeyUp(0x45, 0x58, 0x54, 0x45).ToArray());
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleasePrimary, GlobalInputAction.ReleaseEnglishDictation },
            GlobalInputRouter.OnHotkeyKeyUp(0x45, 0x45, 0x54, 0x45).ToArray());
        Assert.AreEqual(0, GlobalInputRouter.OnHotkeyKeyUp(0x45, 0x58, 0x54, null).Count());
    }

    [TestMethod]
    public void The_english_dictation_hotkey_is_registered_with_the_others_and_conflicts_are_refused()
    {
        // Rarely used F13-F24 chords keep this native test clear of hotkeys a desktop already holds.
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        Hotkey primary = new(modifiers, 0x80);
        Hotkey board = new(modifiers, 0x81);
        Hotkey task = new(modifiers, 0x82);
        Hotkey english = new(modifiers, 0x83);
        using var input = new GlobalInput(() => RecordingState.Idle, _ => { });

        Assert.IsTrue(input.Start(primary, board, task, english));
        Assert.IsTrue(input.EnglishDictationHotkeyAvailable);

        // A chord that another WinWispr hotkey uses is refused, in both directions, and nothing is persisted.
        var persisted = new List<Hotkey>();
        Assert.IsFalse(input.TryReplaceEnglishDictation(task, persisted.Add));
        Assert.IsFalse(input.TryReplaceEnglishDictation(board, persisted.Add));
        Assert.IsFalse(input.TryReplaceEnglishDictation(primary, persisted.Add));
        Assert.IsFalse(input.TryReplaceEnglishDictation(GlobalInput.ReplayHotkey, persisted.Add));
        Assert.IsFalse(input.TryReplaceTask(english, persisted.Add));
        Assert.IsFalse(input.TryReplaceBoard(english, persisted.Add));
        Assert.AreEqual(0, persisted.Count);
        Assert.IsTrue(input.EnglishDictationHotkeyAvailable, "A refused replacement keeps the registered hotkey.");

        // A free chord replaces it at runtime and is persisted once.
        Hotkey replacement = new(modifiers, 0x84);
        Assert.IsTrue(input.TryReplaceEnglishDictation(replacement, persisted.Add));
        CollectionAssert.AreEqual(new[] { replacement }, persisted);
        Assert.IsTrue(input.EnglishDictationHotkeyAvailable);
        // The old chord is free again for another hotkey.
        Assert.IsTrue(input.TryReplaceTask(english, _ => { }));
    }

    [TestMethod]
    public void An_english_dictation_hotkey_that_another_program_holds_is_reported_unavailable_and_keeps_the_others()
    {
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        Hotkey english = new(modifiers, 0x85);
        using var holder = new GlobalInput(() => RecordingState.Idle, _ => { });
        Assert.IsTrue(holder.Start(new Hotkey(modifiers, 0x86), default, default, english),
            "The test could not reserve its hotkeys.");
        using var input = new GlobalInput(() => RecordingState.Idle, _ => { });

        Assert.IsTrue(input.Start(new Hotkey(modifiers, 0x87), new Hotkey(modifiers, 0x7C), new Hotkey(modifiers, 0x7D), english));

        Assert.IsFalse(input.EnglishDictationHotkeyAvailable);
        Assert.IsTrue(input.BoardHotkeyAvailable);
        Assert.IsTrue(input.TaskHotkeyAvailable);
        var persisted = new List<Hotkey>();
        Assert.IsFalse(input.TryReplaceEnglishDictation(english, persisted.Add));
        Assert.AreEqual(0, persisted.Count);
        Assert.IsTrue(input.TryReplaceEnglishDictation(new Hotkey(modifiers, 0x7E), persisted.Add));
        Assert.IsTrue(input.EnglishDictationHotkeyAvailable);
    }

    [TestMethod]
    public void The_translate_hotkey_is_not_a_trigger_and_has_no_release()
    {
        Assert.AreEqual(GlobalInputAction.PressTranslate, GlobalInputRouter.OnTranslateHotkey().Action);
        Assert.IsFalse(GlobalInputRouter.OnTranslateHotkey().Suppress);
        // Only the dictation hotkeys release; a key-up of the translate key never reaches the dictation session.
        Assert.AreEqual(0, GlobalInputRouter.OnHotkeyKeyUp(0x45, 0x58, 0x54, null).Count());
        CollectionAssert.AreEqual(new[] { GlobalInputAction.ReleaseEnglishDictation },
            GlobalInputRouter.OnHotkeyKeyUp(0x45, 0x58, 0x54, 0x45).ToArray());
    }

    [TestMethod]
    public void Esc_cancels_a_translation_that_runs_and_is_suppressed_like_the_recording_cancel_key()
    {
        var router = new GlobalInputRouter();

        HookDecision down = router.OnKeyDown(0x1B, RecordingState.Idle, translating: true);

        Assert.AreEqual(GlobalInputAction.CancelTranslation, down.Action);
        Assert.IsTrue(down.Suppress);
        // Auto-repeat does not cancel again, and the key-up is swallowed with the key-down.
        HookDecision repeat = router.OnKeyDown(0x1B, RecordingState.Idle, translating: true);
        Assert.AreEqual(GlobalInputAction.None, repeat.Action);
        Assert.IsTrue(repeat.Suppress);
        Assert.IsTrue(router.OnKeyUp(0x1B, RecordingState.Idle).Suppress);
        Assert.AreEqual(GlobalInputAction.CancelTranslation, router.OnKeyDown(0x1B, RecordingState.Idle, translating: true).Action);
    }

    [TestMethod]
    public void Esc_passes_through_to_other_applications_when_no_translation_runs()
    {
        var router = new GlobalInputRouter();

        HookDecision decision = router.OnKeyDown(0x1B, RecordingState.Idle, translating: false);

        Assert.AreEqual(GlobalInputAction.None, decision.Action);
        Assert.IsFalse(decision.Suppress);
        Assert.AreEqual(GlobalInputAction.None, router.OnKeyDown(0x1B, RecordingState.Idle).Action);
        Assert.IsFalse(router.OnKeyUp(0x1B, RecordingState.Idle).Suppress);
    }

    [TestMethod]
    public void A_recording_keeps_its_own_cancel_handling_of_esc()
    {
        var router = new GlobalInputRouter();

        Assert.AreEqual(GlobalInputAction.RequestCancel,
            router.OnKeyDown(0x1B, RecordingState.Recording, translating: false).Action);
    }

    [TestMethod]
    public void The_translate_hotkey_is_registered_with_the_others_and_conflicts_are_refused()
    {
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        Hotkey primary = new(modifiers, 0x88);
        Hotkey board = new(modifiers, 0x89);
        Hotkey task = new(modifiers, 0x8A);
        Hotkey english = new(modifiers, 0x8B);
        Hotkey translate = new(modifiers, 0x8C);
        using var input = new GlobalInput(() => RecordingState.Idle, _ => { });

        Assert.IsTrue(input.Start(primary, board, task, english, translate));
        Assert.IsTrue(input.TranslateHotkeyAvailable);

        var persisted = new List<Hotkey>();
        Assert.IsFalse(input.TryReplaceTranslate(task, persisted.Add));
        Assert.IsFalse(input.TryReplaceTranslate(board, persisted.Add));
        Assert.IsFalse(input.TryReplaceTranslate(english, persisted.Add));
        Assert.IsFalse(input.TryReplaceTranslate(primary, persisted.Add));
        Assert.IsFalse(input.TryReplaceTranslate(GlobalInput.ReplayHotkey, persisted.Add));
        Assert.IsFalse(input.TryReplaceTask(translate, persisted.Add));
        Assert.IsFalse(input.TryReplaceBoard(translate, persisted.Add));
        Assert.IsFalse(input.TryReplaceEnglishDictation(translate, persisted.Add));
        Assert.AreEqual(0, persisted.Count);
        Assert.IsTrue(input.TranslateHotkeyAvailable, "A refused replacement keeps the registered hotkey.");

        Hotkey replacement = new(modifiers, 0x8D);
        Assert.IsTrue(input.TryReplaceTranslate(replacement, persisted.Add));
        CollectionAssert.AreEqual(new[] { replacement }, persisted);
        Assert.IsTrue(input.TranslateHotkeyAvailable);
        Assert.IsTrue(input.TryReplaceTask(translate, _ => { }), "The old chord is free again.");
    }

    [TestMethod]
    public void A_translate_hotkey_that_another_program_holds_is_reported_unavailable_and_keeps_the_others()
    {
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        Hotkey translate = new(modifiers, 0x8E);
        using var holder = new GlobalInput(() => RecordingState.Idle, _ => { });
        Assert.IsTrue(holder.Start(new Hotkey(modifiers, 0x8F), default, default, default, translate),
            "The test could not reserve its hotkeys.");
        using var input = new GlobalInput(() => RecordingState.Idle, _ => { });

        Assert.IsTrue(input.Start(
            new Hotkey(modifiers, 0x7F), new Hotkey(modifiers, 0x7C), new Hotkey(modifiers, 0x7D), default, translate));

        Assert.IsFalse(input.TranslateHotkeyAvailable);
        Assert.IsTrue(input.BoardHotkeyAvailable);
        Assert.IsTrue(input.TaskHotkeyAvailable);
        var persisted = new List<Hotkey>();
        Assert.IsFalse(input.TryReplaceTranslate(translate, persisted.Add));
        Assert.AreEqual(0, persisted.Count);
        Assert.IsTrue(input.TryReplaceTranslate(new Hotkey(modifiers, 0x7E), persisted.Add));
        Assert.IsTrue(input.TranslateHotkeyAvailable);
    }

    [TestMethod]
    public void A_hotkey_can_be_registered_after_the_initial_registration_failed()
    {
        Hotkey candidate = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);
        var persisted = new List<Hotkey>();
        var registration = new HotkeyRegistration(null, _ => true,
            _ => Assert.Fail("No previous hotkey was registered."), persisted.Add);

        Assert.IsTrue(registration.TryReplace(candidate));
        Assert.AreEqual(candidate, registration.Active);
        CollectionAssert.AreEqual(new[] { candidate }, persisted);
    }

    [TestMethod]
    public void Board_and_task_hotkeys_stay_registered_when_another_program_holds_the_primary_hotkey()
    {
        // Rarely used F13-F16 chords keep this native test clear of hotkeys a desktop already holds.
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        Hotkey primary = new(modifiers, 0x7C);
        Hotkey board = new(modifiers, 0x7D);
        Hotkey task = new(modifiers, 0x7E);
        using var holder = new GlobalInput(() => RecordingState.Idle, _ => { });
        Assert.IsTrue(holder.Start(primary), "The test could not reserve its primary hotkey.");
        using var input = new GlobalInput(() => RecordingState.Idle, _ => { });

        Assert.IsFalse(input.Start(primary, board, task));
        Assert.IsTrue(input.BoardHotkeyAvailable);
        Assert.IsTrue(input.TaskHotkeyAvailable);

        using var probe = new GlobalInput(() => RecordingState.Idle, _ => { });
        Assert.IsTrue(probe.Start(new Hotkey(modifiers, 0x7F), board, task));
        Assert.IsFalse(probe.BoardHotkeyAvailable, "The board hotkey must still be registered by the first instance.");
        Assert.IsFalse(probe.TaskHotkeyAvailable, "The task hotkey must still be registered by the first instance.");
    }

    [TestMethod]
    public void Failed_rollback_allows_a_later_fresh_registration()
    {
        bool canRegister = false;
        var registration = new HotkeyRegistration(AppSettings.Default.Hotkey,
            _ => canRegister, _ => { }, _ => { });
        Hotkey candidate = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);

        Assert.IsFalse(registration.TryReplace(candidate));
        Assert.IsNull(registration.Active);
        canRegister = true;
        Assert.IsTrue(registration.TryReplace(candidate));
        Assert.AreEqual(candidate, registration.Active);
    }

    [TestMethod]
    public void Y_is_consumed_only_while_cancel_is_pending()
    {
        var router = new GlobalInputRouter();

        HookDecision pending = router.OnKeyDown(0x59, RecordingState.CancelPending);
        HookDecision idle = router.OnKeyDown(0x59, RecordingState.Idle);

        Assert.AreEqual(GlobalInputAction.ConfirmDiscard, pending.Action);
        Assert.IsTrue(pending.Suppress);
        Assert.AreEqual(GlobalInputAction.None, idle.Action);
        Assert.IsFalse(idle.Suppress);
    }

    [TestMethod]
    public void Escape_requests_cancel_and_suppresses_keyup()
    {
        var router = new GlobalInputRouter();

        HookDecision down = router.OnKeyDown(0x1B, RecordingState.Recording);
        HookDecision up = router.OnKeyUp(0x1B, RecordingState.CancelPending);

        Assert.AreEqual(GlobalInputAction.RequestCancel, down.Action);
        Assert.IsTrue(down.Suppress);
        Assert.IsTrue(up.Suppress);
    }

    [TestMethod]
    public void Y_after_escape_is_confirmed_before_controller_state_is_updated()
    {
        var router = new GlobalInputRouter();

        router.OnKeyDown(0x1B, RecordingState.Recording);
        HookDecision y = router.OnKeyDown(0x59, RecordingState.Recording);

        Assert.AreEqual(GlobalInputAction.ConfirmDiscard, y.Action);
        Assert.IsTrue(y.Suppress);
    }

    [TestMethod]
    public void Y_auto_repeat_and_keyup_are_suppressed_without_second_confirmation()
    {
        var router = new GlobalInputRouter();

        HookDecision first = router.OnKeyDown(0x59, RecordingState.CancelPending);
        HookDecision repeat = router.OnKeyDown(0x59, RecordingState.CancelPending);
        HookDecision up = router.OnKeyUp(0x59, RecordingState.Idle);

        Assert.AreEqual(GlobalInputAction.ConfirmDiscard, first.Action);
        Assert.AreEqual(GlobalInputAction.None, repeat.Action);
        Assert.IsTrue(repeat.Suppress);
        Assert.IsTrue(up.Suppress);
    }

    [TestMethod]
    public void Invalid_recording_chords_are_rejected()
    {
        Assert.IsFalse(HotkeyRegistration.IsValid(new Hotkey(HotkeyModifiers.None, 0x58)));
        Assert.IsFalse(HotkeyRegistration.IsValid(new Hotkey(HotkeyModifiers.Shift, 0x1B)));
        Assert.IsFalse(HotkeyRegistration.IsValid(new Hotkey(HotkeyModifiers.Shift, 0x59)));
        Assert.IsFalse(HotkeyRegistration.IsValid(new Hotkey(HotkeyModifiers.Shift, 0)));
        Assert.IsTrue(HotkeyRegistration.IsValid(AppSettings.Default.Hotkey));
    }

    [TestMethod]
    public void Capture_tracks_each_modifier_from_the_hook_event()
    {
        Assert.IsTrue(GlobalInput.TryGetModifierVirtualKey(0xA2, out HotkeyModifiers leftControl));
        Assert.AreEqual(HotkeyModifiers.Control, leftControl);
        Assert.IsTrue(GlobalInput.TryGetModifierVirtualKey(0xA5, out HotkeyModifiers rightAlt));
        Assert.AreEqual(HotkeyModifiers.Alt, rightAlt);
        Assert.IsTrue(GlobalInput.TryGetModifierVirtualKey(0xA0, out HotkeyModifiers leftShift));
        Assert.AreEqual(HotkeyModifiers.Shift, leftShift);
        Assert.IsTrue(GlobalInput.TryGetModifierVirtualKey(0x5B, out HotkeyModifiers leftWindows));
        Assert.AreEqual(HotkeyModifiers.Windows, leftWindows);
        Assert.IsFalse(GlobalInput.TryGetModifierVirtualKey(0x41, out _));
    }

    [TestMethod]
    public void Failed_replacement_restores_previous_registration_and_does_not_persist()
    {
        Hotkey previous = AppSettings.Default.Hotkey;
        Hotkey candidate = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41);
        var registered = new List<Hotkey>();
        var unregistered = new List<Hotkey>();
        var persisted = new List<Hotkey>();
        var replacement = new HotkeyRegistration(
            previous,
            hotkey =>
            {
                registered.Add(hotkey);
                return hotkey == previous;
            },
            hotkey => unregistered.Add(hotkey),
            hotkey => persisted.Add(hotkey));

        Assert.IsFalse(replacement.TryReplace(candidate));
        Assert.AreEqual(previous, replacement.Active);
        CollectionAssert.AreEqual(new[] { previous, candidate }, unregistered);
        CollectionAssert.AreEqual(new[] { candidate, previous }, registered);
        Assert.AreEqual(0, persisted.Count);
    }

    [TestMethod]
    public void Successful_replacement_persists_only_after_registration()
    {
        Hotkey previous = AppSettings.Default.Hotkey;
        Hotkey candidate = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41);
        var persisted = new List<Hotkey>();
        var replacement = new HotkeyRegistration(
            previous,
            _ => true,
            _ => { },
            hotkey => persisted.Add(hotkey));

        Assert.IsTrue(replacement.TryReplace(candidate));
        Assert.AreEqual(candidate, replacement.Active);
        CollectionAssert.AreEqual(new[] { candidate }, persisted);
    }
}
