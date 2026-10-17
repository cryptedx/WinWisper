# WinWispr domain glossary

Terms the code, tests, issues and reviews use for WinWispr concepts. Use them exactly.

## Dictation

**Dictation session**
: One recording from its accepting press to its return to Idle. It covers preparation, recording, the cancel window, recognition and delivery. The `DictationSession` module owns the session lifecycle: the owner trigger, the mode, the destination, the target window and the pending start.

**Trigger**
: An input that can start a dictation session or end the one it owns. The triggers are the primary hotkey, the task hotkey, the English dictation hotkey, the task board's dictate button (the *task button*) and the tray. The translate hotkey is not a trigger: it starts a selection translation, never a dictation session. The overlay Stop button, the recording limit and a microphone failure are *stop sources*, not triggers.

**Owner trigger**
: The trigger whose press created the current dictation session. The session ends through the owner's release (Hold), a second press of the owner (Toggle), a tray press or a stop source. Presses from other triggers are ignored while a session is owned.

**Destination**
: Where a finished transcript goes. *Text* means it is pasted into the target window. *Task* means it is parsed into a task draft for the task board. The session derives the destination from its trigger when the press is accepted.

**Pending start**
: A press accepted while the speech model is still loading. When the model becomes ready, the pending start begins recording with the target, destination and mode captured at the press. A Hold release, a stop, or a model that fails or goes missing drops it.

**Recording mode**
: *Hold* records while the trigger is held down. *Toggle* starts on one press and stops on the next. The session fixes the mode at the press, so settings changes apply from the next session. The tray is always Toggle.

**Meeting mute**
: While a dictation session records, WinWispr mutes the user's Teams meeting microphone so the meeting does not hear the dictation, and gives it back as soon as the capture ends. The session orders it around the capture: mute before the capture starts; restore as soon as the capture stops, is discarded or fails, and on shutdown. A mute that cannot be confirmed stops the session from capturing; a restore that cannot be confirmed reports a dictation notice. A mute the user made themselves is never touched.

**Capture device**
: The source of audio samples for a dictation session, behind the `ICaptureDevice` seam. In the app it is the microphone through WinMM; in the E2E profile it can be a recorded clip played back in real time (the *replay device*). The device feeds the session's audio buffer between start and stop. A replayed clip never reaches a meeting, so the E2E profile leaves the meeting mute out. `AudioCapture` owns the buffer, the lifecycle, and the limit, peak, and failure signals, and hands the device's blocking work to the thread pool.

**Microphone selection**
: Which capture device a dictation session uses. The *saved* microphone is the user's preference in the settings; the *effective* microphone is the one in use. When the saved microphone cannot be selected (it is missing, or no device or no list is available), the Windows default stands in for it and the saved preference stays unchanged. `MicrophoneCatalog` lists the devices once and applies these rules for the settings, the tray and the session. The session always asks for the saved microphone; the catalog checks at capture start whether it is connected, matching the WinMM device by its audio endpoint. If it is not, that session records from the Windows default and reports a *Windows default microphone used* notice when it ends, unless an outcome notice takes its place.

**Dictation notice**
: An outcome or problem that the dictation session reports to the user after a press, such as *No speech* or *Copied to clipboard*, including a warning that the meeting mute could not be restored. A notice is typed by its kind; its visible text, detail line and display duration come from one presentation mapping. Overlay statuses that App shows on its own, such as "Loading model", are not dictation notices.

## Translation

**Selection translation**
: Translating text the user has selected in another application between German and English. The source language is detected and the translation goes into the other language. The result appears in the translation preview; the selection only changes when the user chooses to replace it. The personal dictionary does not apply. A selection translation is not a dictation session. It starts from the translate hotkey, or from the tray, where it translates the clipboard instead of a selection.

**Translate hotkey**
: The global hotkey that starts a selection translation. It is not a trigger: it never starts a dictation session.

**Selection reader**
: The part of a selection translation that reads the selected text: it waits until the user has released the translate hotkey's modifier keys, sends Ctrl+C, notices the copy by the clipboard's change counter (never by comparing text, so a selection equal to the clipboard text still counts), reads the text and puts the previous clipboard back on every path. It restores the standard formats (text, HTML, RTF, bitmap, files, web link); an application's private formats cannot be restored. The target application's own copy can still appear in the Windows clipboard history.

**Dictation translation**
: A dictation session whose transcript is translated into English before delivery. The personal dictionary applies to the transcript before translation. A transcript that is already English is delivered unchanged. Its owner trigger is the English dictation hotkey, and its destination is always Text.

**English dictation hotkey**
: The trigger that starts a dictation translation. It has its own recording mode, like the task hotkey.

**Translation preview**
: The panel that shows the result of a selection translation. From it the user replaces the selection with the translation, copies the translation, dismisses it, or translates again in the opposite direction. A preview opened from the tray cannot replace, because there is no selection.

**Translation notice**
: An outcome or problem that a selection translation reports to the user, such as *No text selected*, *Selection too long* or *Translation not installed*. It is shown the way dictation notices are shown but is its own kind. Problems of a dictation translation, such as *Translation failed – transcript copied*, stay dictation notices because they come from a dictation session.

## Models

**Model set**
: One group of model files that WinWispr treats as a unit. The speech model is installed from one pinned archive into its own folder of the app data with its own active pointer; `ModelInstaller` owns it. The translation model ships as loose files in WinWispr Models and is read in place in the installation folder. Either set counts as installed only when every one of its files is present at its pinned size, and that answer comes from the files, never from `settings.json`.

**Translation model**
: The model set of the offline translation engine, holding both directions (German to English and English to German) as ten files. It ships in WinWispr Models, which puts it in the `TranslationData` folder next to the executable; it is never downloaded, and Setup leaves it in place. When a file is missing or has the wrong size, the features that need it report *Translation not installed*, and running the WinWispr Models installer adds or restores it.

**WinWispr Models**
: The separate installer `WinWispr-Models-vN.exe` that carries the speech model archive (`ModelData`), the translation model (`TranslationData`) and the offline voices (`VoiceData`), which Setup and Portable do not contain. It installs them into an existing WinWispr folder next to `WinWispr.exe` and removes exactly those files on uninstall. It is released on its own as `models-vN`, apart from the app releases. Avoid calling it an update or a model download.

**Model package version**
: The integer `N` of WinWispr Models, recorded in `eng/model-package.json` together with the pins of its content. Any change to a pinned model needs a new model package version; app versions (`vMAJOR.MINOR.PATCH`) are independent of it.

## Updates

**Update check**
: WinWispr's anonymous look at the latest GitHub release `vX.Y.Z`, automatic (30 seconds after start, then every 24 hours, while `AutomaticUpdateChecks` is on) or on demand. A newer version is offered in a tray balloon; only when the user clicks it does WinWispr download Setup, verify its SHA-256 and its pinned WinWispr signature, and run it silently into the same folder and scope. Portable copies only get a link to the release page. The machine policy `DisableUpdateCheck` turns off both kinds. It never covers model releases `models-vN`: WinWispr Models is not an update.

## Settings

**Personal dictionary**
: The user's phrase = replacement entries, applied to every transcript. It is user data, not a preference: it lives in its own file (`settings.json.dictionary` with a `.bak`), independent of `settings.json`, and must survive updates, settings resets and settings load failures. `PersonalDictionaryStore` owns the file; `PersonalDictionary` is the parsed value the dictation session applies.

**Settings**
: The user's preferences in `settings.json`: hotkeys, modes, microphone, speech mode and window placement. `AppSettingsStore` saves the whole record; a settings failure never blocks or rewrites the personal dictionary.
