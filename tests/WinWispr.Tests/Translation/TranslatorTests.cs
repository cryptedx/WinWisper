using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The Translator through its interface, with a fake engine behind it, so that no model and no native runtime are
/// needed: result kinds, lazy loading, load state, preload, retry and serialization.
/// </summary>
[TestClass]
public sealed class TranslatorTests
{
    private const TranslationDirection DeEn = TranslationDirection.GermanToEnglish;
    private const TranslationDirection EnDe = TranslationDirection.EnglishToGerman;

    [TestMethod]
    public async Task Translates_with_the_engine_of_the_requested_direction()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        var toEnglish = await translator.TranslateAsync("Hallo", DeEn);
        var toGerman = await translator.TranslateAsync("Hello", EnDe);

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Hallo", DeEn), toEnglish);
        Assert.AreEqual(new TranslationResult.Translated("[en-de] Hello", EnDe), toGerman);
    }

    [TestMethod]
    public void Nothing_is_resolved_or_loaded_when_the_module_is_constructed()
    {
        var factory = new FakeTranslationEngines();
        int resolved = 0;

        using var translator = new Translator(() => { resolved++; return "models"; }, factory.Create);

        Assert.AreEqual(0, resolved);
        Assert.AreEqual(0, factory.Loads);
        Assert.IsFalse(translator.IsLoaded(DeEn));
        Assert.IsFalse(translator.IsLoadPending(DeEn));
    }

    [TestMethod]
    public async Task First_translation_loads_the_direction_and_later_ones_reuse_it()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        await translator.TranslateAsync("eins", DeEn);
        Assert.IsTrue(translator.IsLoaded(DeEn));
        Assert.IsFalse(translator.IsLoadPending(DeEn));
        await translator.TranslateAsync("zwei", DeEn);
        await translator.TranslateAsync("drei", DeEn);

        Assert.AreEqual(1, factory.Loads);
    }

    [TestMethod]
    public async Task Directions_load_independently_and_only_when_used()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsTrue(translator.IsLoaded(DeEn));
        Assert.IsFalse(translator.IsLoaded(EnDe));
        CollectionAssert.AreEqual(new[] { DeEn }, factory.LoadedDirections);
    }

    [TestMethod]
    public async Task Preload_returns_at_once_and_a_translation_meanwhile_waits_for_that_load()
    {
        var factory = new FakeTranslationEngines { Gate = new ManualResetEventSlim(false) };
        using var translator = new Translator(() => "models", factory.Create);

        translator.Preload(DeEn);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(translator.IsLoadPending(DeEn), "the load is still running");
        Assert.IsFalse(translator.IsLoaded(DeEn));

        Task<TranslationResult> waiting = translator.TranslateAsync("Hallo", DeEn);
        await Task.Delay(100);
        Assert.IsFalse(waiting.IsCompleted, "the translation waits for the load");

        factory.Gate.Set();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Hallo", DeEn), result);
        Assert.AreEqual(1, factory.Loads, "a translation during a load must not start a second load");
        Assert.IsFalse(translator.IsLoadPending(DeEn));
        Assert.IsTrue(translator.IsLoaded(DeEn));
    }

    [TestMethod]
    public void Preload_of_a_loaded_direction_does_nothing()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        translator.Preload(DeEn);
        translator.Preload(DeEn);
        SpinWait.SpinUntil(() => translator.IsLoaded(DeEn), TimeSpan.FromSeconds(10));
        translator.Preload(DeEn);

        Assert.AreEqual(1, factory.Loads);
    }

    [TestMethod]
    public async Task A_translation_during_a_pending_load_can_be_cancelled_without_stopping_the_load()
    {
        var factory = new FakeTranslationEngines { Gate = new ManualResetEventSlim(false) };
        using var translator = new Translator(() => "models", factory.Create);
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> waiting = translator.TranslateAsync("Hallo", DeEn, cancellation.Token);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => waiting);

        factory.Gate.Set();
        var result = await translator.TranslateAsync("Hallo", DeEn).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Hallo", DeEn), result);
        Assert.AreEqual(1, factory.Loads);
    }

    [TestMethod]
    public async Task A_cancelled_token_throws_before_any_work()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => translator.TranslateAsync("Hallo", DeEn, cancellation.Token));

        Assert.AreEqual(0, factory.Loads);
    }

    [TestMethod]
    public async Task Cancellation_stops_a_running_translation()
    {
        var factory = new FakeTranslationEngines { BlockTranslation = new ManualResetEventSlim(false) };
        using var translator = new Translator(() => "models", factory.Create);
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> running = translator.TranslateAsync("Hallo", DeEn, cancellation.Token);
        await factory.TranslationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        factory.BlockTranslation.Set();
        var next = await translator.TranslateAsync("Noch einmal", DeEn).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<TranslationResult.Translated>(next);
    }

    [TestMethod]
    public async Task A_missing_model_directory_is_not_installed_and_nothing_is_loaded()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => null, factory.Create);

        var result = await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsInstanceOfType<TranslationResult.NotInstalled>(result);
        Assert.AreEqual(0, factory.Loads);
        Assert.IsFalse(translator.IsLoaded(DeEn));
        Assert.IsFalse(translator.IsLoadPending(DeEn));
    }

    [TestMethod]
    public async Task An_engine_that_finds_no_files_is_not_installed_and_installing_later_works_without_a_restart()
    {
        var factory = new FakeTranslationEngines { Installed = false };
        using var translator = new Translator(() => "models", factory.Create);

        var before = await translator.TranslateAsync("Hallo", DeEn);
        factory.Installed = true;
        var after = await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsInstanceOfType<TranslationResult.NotInstalled>(before);
        Assert.AreEqual(new TranslationResult.Translated("[de-en] Hallo", DeEn), after);
    }

    [TestMethod]
    public async Task A_load_that_throws_is_a_failed_result_and_a_later_request_retries_it()
    {
        var observed = new List<Exception>();
        var factory = new FakeTranslationEngines { FailLoads = 1 };
        using var translator = new Translator(() => "models", factory.Create, observed.Add);

        var first = await translator.TranslateAsync("Geheimer Text", DeEn);
        Assert.IsFalse(translator.IsLoaded(DeEn));
        Assert.IsFalse(translator.IsLoadPending(DeEn));
        var second = await translator.TranslateAsync("Geheimer Text", DeEn);

        var failed = (TranslationResult.Failed)first;
        Assert.IsFalse(failed.Reason.Contains("Geheimer", StringComparison.Ordinal), "a failure never carries the text");
        Assert.AreEqual(1, observed.Count);
        Assert.AreEqual(new TranslationResult.Translated("[de-en] Geheimer Text", DeEn), second);
        Assert.AreEqual(2, factory.Loads);
    }

    [TestMethod]
    public async Task A_failing_resolver_is_a_failed_result_not_an_exception()
    {
        using var translator = new Translator(() => throw new IOException("disk gone"), new FakeTranslationEngines().Create);

        var result = await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsInstanceOfType<TranslationResult.Failed>(result);
    }

    [TestMethod]
    public async Task A_sentence_the_engine_cannot_translate_is_failed_and_the_next_sentence_still_works()
    {
        var factory = new FakeTranslationEngines { RejectText = "kaputt" };
        using var translator = new Translator(() => "models", factory.Create);

        var bad = await translator.TranslateAsync("kaputt", DeEn);
        var good = await translator.TranslateAsync("heil", DeEn);

        Assert.IsInstanceOfType<TranslationResult.Failed>(bad);
        Assert.AreEqual(new TranslationResult.Translated("[de-en] heil", DeEn), good);
        Assert.AreEqual(1, factory.Loads, "a failed sentence does not unload the model");
    }

    [TestMethod]
    public async Task A_throwing_failure_hook_never_breaks_the_result()
    {
        var factory = new FakeTranslationEngines { FailLoads = 1 };
        using var translator = new Translator(
            () => "models", factory.Create, _ => throw new InvalidOperationException("log is broken"));

        var result = await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsInstanceOfType<TranslationResult.Failed>(result);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\r\n\t ")]
    public async Task Whitespace_translates_to_empty_text_without_loading_anything(string text)
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        var result = await translator.TranslateAsync(text, DeEn);

        Assert.AreEqual(new TranslationResult.Translated(string.Empty, DeEn), result);
        Assert.AreEqual(0, factory.Loads);
    }

    [TestMethod]
    public async Task Concurrent_translations_run_one_after_the_other_and_both_return_their_own_result()
    {
        var factory = new FakeTranslationEngines { Pause = TimeSpan.FromMilliseconds(40) };
        using var translator = new Translator(() => "models", factory.Create);

        Task<TranslationResult>[] all = Enumerable.Range(0, 6)
            .Select(i => translator.TranslateAsync($"Satz {i}", i % 2 == 0 ? DeEn : EnDe))
            .ToArray();
        TranslationResult[] results = await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(20));

        for (int i = 0; i < results.Length; i++)
        {
            string prefix = i % 2 == 0 ? "[de-en]" : "[en-de]";
            Assert.AreEqual(new TranslationResult.Translated($"{prefix} Satz {i}", i % 2 == 0 ? DeEn : EnDe), results[i]);
        }

        Assert.AreEqual(1, factory.MaxConcurrentTranslations, "inference is serialized across directions too");
    }

    [TestMethod]
    public async Task Disposing_disposes_the_loaded_engines_and_later_requests_fail_without_throwing()
    {
        var factory = new FakeTranslationEngines();
        var translator = new Translator(() => "models", factory.Create);
        await translator.TranslateAsync("Hallo", DeEn);
        await translator.TranslateAsync("Hello", EnDe);

        translator.Dispose();
        var result = await translator.TranslateAsync("Hallo", DeEn);

        Assert.AreEqual(2, factory.DisposedEngines);
        Assert.IsInstanceOfType<TranslationResult.Failed>(result);
        translator.Preload(DeEn);
    }

    [TestMethod]
    public async Task The_default_engine_reports_not_installed_for_a_missing_or_incomplete_directory()
    {
        using var root = new TemporaryDirectory();
        string missing = Path.Combine(root.Path, "does-not-exist");
        string partial = Path.Combine(root.Path, "partial");
        await root.WriteTextAsync("partial/opus-mt-de-en/source.spm", "x");
        await root.WriteTextAsync("partial/opus-mt-de-en/vocab.json", "{}");
        string empty = Path.Combine(root.Path, "empty");
        Directory.CreateDirectory(empty);

        foreach (string directory in new[] { missing, partial, empty })
        {
            using var translator = new Translator(() => directory);
            var result = await translator.TranslateAsync("Hallo", DeEn);
            Assert.IsInstanceOfType<TranslationResult.NotInstalled>(result, directory);
        }
    }

    [TestMethod]
    public async Task The_default_engine_reports_a_damaged_model_as_failed_and_never_throws()
    {
        using var root = new TemporaryDirectory();
        foreach (string relative in OpusMtEngine.RequiredFiles)
            await root.WriteTextAsync($"opus-mt-de-en/{relative.Replace('\\', '/')}", "this is not a model");
        var observed = new List<Exception>();
        using var translator = new Translator(() => root.Path, observed.Add);

        var first = await translator.TranslateAsync("Hallo", DeEn);
        var second = await translator.TranslateAsync("Hallo", DeEn);

        Assert.IsInstanceOfType<TranslationResult.Failed>(first);
        Assert.IsInstanceOfType<TranslationResult.Failed>(second, "a failed load is retried, and fails again");
        Assert.AreEqual(2, observed.Count);
        Assert.IsFalse(translator.IsLoaded(DeEn));
    }

    [TestMethod]
    public void The_output_limit_grows_with_the_sentence_and_is_capped()
    {
        Assert.AreEqual(10, OpusMtEngine.MaxOutputTokens(1));
        Assert.AreEqual(58, OpusMtEngine.MaxOutputTokens(25));
        Assert.AreEqual(264, OpusMtEngine.MaxOutputTokens(OpusMtEngine.MaxSourceTokens));
        Assert.AreEqual(512, OpusMtEngine.MaxOutputTokens(10_000));
    }
}
