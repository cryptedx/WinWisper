using WinWispr.Core;

namespace WinWispr.Speech;

internal sealed class TtsSession
{
    private readonly Func<string, TtsEngine> _load;
    private readonly Func<TtsAudio, CancellationToken, Task> _play;
    private readonly Action _pauseOutput, _resumeOutput, _stopOutput;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _gate = new();
    private TtsEngine? _engine;
    private CancellationTokenSource? _speech;
    private TaskCompletionSource _resumed = NewSignal();
    private bool _paused, _dictating;

    internal TtsSession(Func<string, TtsEngine> load, Func<TtsAudio, CancellationToken, Task> play,
        Action pauseOutput, Action resumeOutput, Action stopOutput)
    {
        _load = load; _play = play;
        _pauseOutput = pauseOutput; _resumeOutput = resumeOutput; _stopOutput = stopOutput;
        _resumed.TrySetResult();
    }

    internal async Task LoadAsync(string directory)
    {
        Stop();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            _engine?.Unload();
            _engine = null;
            _engine = await Task.Run(() => _load(directory)).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    internal async Task UnloadAsync()
    {
        Stop();
        // Native inference must return before the engine can be freed.
        await _operation.WaitAsync().ConfigureAwait(false);
        try { _engine?.Unload(); _engine = null; }
        finally { _operation.Release(); }
    }

    internal async Task SpeakAsync(string text, float speed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!float.IsFinite(speed) || speed is < 0.5f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (text.Contains('\0')) throw new ArgumentException("Text contains a null character.", nameof(text));
        if (text.Length > 100_000) throw new ArgumentException("Text exceeds the prototype limit.", nameof(text));
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Wait for the current voice operation.");
        using var speech = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            TtsEngine engine = _engine ?? throw new InvalidOperationException("Load a voice first.");
            lock (_gate) { _speech = speech; }
            foreach (string section in Sections(text))
            {
                Task<TtsAudio> generation;
                while (true)
                {
                    await WaitForResumeAsync(speech.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        speech.Token.ThrowIfCancellationRequested();
                        if (_paused) continue;
                        generation = Task.Run(() => engine.Generate(section, speed));
                        break;
                    }
                }
                // Deliberately await the native call even after Stop; never free its model early.
                TtsAudio audio = await generation.ConfigureAwait(false);
                Task playing;
                while (true)
                {
                    await WaitForResumeAsync(speech.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        speech.Token.ThrowIfCancellationRequested();
                        if (_paused) continue;
                        playing = _play(audio, speech.Token);
                        break;
                    }
                }
                await playing.ConfigureAwait(false);
            }
        }
        finally
        {
            try { lock (_gate) { _speech = null; _stopOutput(); } }
            finally { _operation.Release(); }
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (!_paused) { _paused = true; _resumed = NewSignal(); }
            _pauseOutput();
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_dictating) throw new InvalidOperationException("Finish dictation before resuming speech.");
            _paused = false;
            _resumeOutput();
            _resumed.TrySetResult();
        }
    }

    internal void Stop()
    {
        lock (_gate) { _speech?.Cancel(); _stopOutput(); }
    }

    internal void OnDictationStateChanged(RecordingState state)
    {
        lock (_gate)
        {
            _dictating = state != RecordingState.Idle;
            if (_dictating)
            {
                try { Pause(); }
                catch (Exception error)
                {
                    // TTS output failure must not strand the dictation controller in Starting.
                    _speech?.Cancel();
                    try { _stopOutput(); }
                    catch (Exception stopError) { Console.Error.WriteLine($"TTS output reset failed: {stopError.Message}"); }
                    Console.Error.WriteLine($"TTS paused after output failure: {error.Message}");
                }
            }
            // Returning to Idle never resumes playback without user action.
        }
    }

    private Task WaitForResumeAsync(CancellationToken token)
    {
        lock (_gate) return _resumed.Task.WaitAsync(token);
    }

    internal static IEnumerable<string> Sections(string text)
    {
        for (int start = 0; start < text.Length;)
        {
            int length = Math.Min(240, text.Length - start);
            if (start + length < text.Length)
            {
                int split = text.LastIndexOfAny(['.', '!', '?', '\n', ' '], start + length - 1, length);
                if (split >= start) length = split - start + 1;
                else if (char.IsHighSurrogate(text[start + length - 1])) --length;
            }
            string section = text.Substring(start, length).Trim();
            start += length;
            if (section.Length > 0) yield return section;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
