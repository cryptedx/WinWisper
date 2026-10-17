using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class TtsSessionTests
{
    [TestMethod]
    public async Task Output_failures_do_not_block_dictation_or_leave_the_operation_gate_locked()
    {
        bool failStop = false;
        int unloads = 0;
        var session = new TtsSession(_ => new TtsEngine(() => ++unloads, (_, _) => new TtsAudio([0], 22050)),
            (_, _) => { failStop = true; return Task.CompletedTask; },
            () => throw new InvalidOperationException("driver pause failed"), () => { },
            () => { if (failStop) throw new InvalidOperationException("driver stop failed"); });
        await session.LoadAsync("voice");
        try { await session.SpeakAsync("test", 1); Assert.Fail(); } catch (InvalidOperationException) { }
        session.OnDictationStateChanged(RecordingState.Starting);
        session.OnDictationStateChanged(RecordingState.Idle);
        failStop = false;
        await session.UnloadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, unloads);
    }

    [TestMethod]
    public async Task Stop_discards_active_result_and_unload_waits_for_native_return()
    {
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        int plays = 0, unloaded = 0, stopped = 0;
        var session = new TtsSession(_ => new TtsEngine(() => ++unloaded, (_, _) =>
        {
            started.Set();
            Assert.IsTrue(finish.Wait(TimeSpan.FromSeconds(5)));
            return new TtsAudio([0.1f], 22050);
        }), (_, _) => { ++plays; return Task.CompletedTask; }, () => { }, () => { }, () => ++stopped);
        await session.LoadAsync("voice");
        Task speech = session.SpeakAsync("Test speech", 1);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        Task unload = session.UnloadAsync();
        Assert.IsFalse(unload.IsCompleted);
        Assert.AreEqual(0, unloaded);
        Assert.IsTrue(stopped > 0);
        finish.Set();
        try { await speech; Assert.Fail("Stopped speech must be cancelled."); }
        catch (OperationCanceledException) { }
        await unload;
        Assert.AreEqual(0, plays);
        Assert.AreEqual(1, unloaded);
    }

    [TestMethod]
    public async Task Dictation_pauses_playback_and_future_generation_until_manual_resume()
    {
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        int generated = 0, plays = 0, pauses = 0;
        var session = new TtsSession(_ => new TtsEngine(() => { }, (_, _) =>
        {
            Interlocked.Increment(ref generated); started.Set();
            Assert.IsTrue(finish.Wait(TimeSpan.FromSeconds(5)));
            return new TtsAudio([0.1f], 22050);
        }), (_, _) => { ++plays; return Task.CompletedTask; }, () => ++pauses, () => { }, () => { });
        await session.LoadAsync("voice");
        Task speech = session.SpeakAsync(new string('x', 500), 1);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        session.OnDictationStateChanged(RecordingState.Starting);
        Assert.ThrowsExactly<InvalidOperationException>(session.Resume);
        finish.Set();
        await Task.Delay(50);
        session.OnDictationStateChanged(RecordingState.Idle);
        await Task.Delay(50);
        Assert.AreEqual(1, generated);
        Assert.AreEqual(0, plays);
        Assert.IsTrue(pauses > 0);
        session.Resume();
        await speech.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, generated);
        Assert.AreEqual(3, plays);
        await session.UnloadAsync();
    }

    [TestMethod]
    public void Sections_bound_unpunctuated_input_and_preserve_surrogate_pairs()
    {
        string text = new string('x', 239) + "😀" + new string('y', 500);
        string[] sections = TtsSession.Sections(text).ToArray();
        Assert.IsTrue(sections.All(s => s.Length <= 240));
        Assert.IsTrue(sections.All(s => !char.IsHighSurrogate(s[^1]) && !char.IsLowSurrogate(s[0])));
        Assert.AreEqual(text, string.Concat(sections));
    }

    [TestMethod]
    public async Task Switching_voices_releases_old_voice_and_failed_load_leaves_none()
    {
        int alive = 0;
        var session = new TtsSession(path =>
        {
            Assert.AreEqual(0, alive);
            if (path == "bad") throw new InvalidDataException();
            ++alive;
            return new TtsEngine(() => --alive, (_, _) => new TtsAudio([0], 22050));
        }, (_, _) => Task.CompletedTask, () => { }, () => { }, () => { });
        await session.LoadAsync("de");
        await session.LoadAsync("en");
        try { await session.LoadAsync("bad"); Assert.Fail(); } catch (InvalidDataException) { }
        Assert.AreEqual(0, alive);
        try { await session.SpeakAsync("test", 1); Assert.Fail(); } catch (InvalidOperationException) { }
    }
}
