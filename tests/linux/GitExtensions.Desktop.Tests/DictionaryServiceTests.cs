using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux.Spelling;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DictionaryServiceTests
{
    [TestCase(null, "en-US")]
    [TestCase(" ", " ")]
    [TestCase("None", "none")]
    [TestCase(" NONE ", " NONE ")]
    [TestCase("en-us", "en-us")]
    public void DisabledIdentityAndDefaultAreCanonical(string? value, string expected)
        => Assert.That(DictionaryCatalog.NormalizeName(value), Is.EqualTo(expected));

    [Test]
    public void CustomBasenamesAreExactAndInvalidIdentitiesCannotBecomeAnInstalledLanguage()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        string custom = Path.Combine(catalog.InstallationDirectory, "custom Team.dic");
        File.Copy(catalog.Resolve("en-US").FilePath, custom);
        Assert.That(catalog.Resolve("custom Team").FilePath, Is.EqualTo(custom));
        Assert.That(catalog.GetInstalled().Select(item => item.Name), Does.Contain("custom Team"));
        Assert.Throws<FileNotFoundException>(() => catalog.Resolve("en-us"));
        Assert.Throws<FileNotFoundException>(() => catalog.Resolve(" en-US "));
        foreach (string invalid in new[] { "", " ", ".", "..", "../en-US", "dir\\en-US", "en-US\0" })
        { Assert.Throws<ArgumentException>(() => catalog.Resolve(invalid), invalid); }
    }

    [Test]
    public void TheThirteenOriginalNetSpellResourcesAndExistingAttributionsAreUnchanged()
    {
        ImmutableArray<SpellDictionary> entries = Installed().GetInstalled();
        Assert.That(entries.Select(item => item.Name), Is.EquivalentTo(new[] { "de-DE", "en-AU", "en-CA", "en-GB", "en-US", "es-ES", "es-MX", "fr-FR", "it-IT", "nl-NL", "pl-PL", "ro-RO", "ru-RU" }));
        Assert.That(entries.Sum(item => new FileInfo(item.FilePath).Length), Is.EqualTo(20_223_005));
        foreach (SpellDictionary entry in entries)
        {
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry.FilePath))), Is.EqualTo(ResourceHashes[entry.Name]), entry.Name);
        }
        Assert.That(entries.Any(item => item.Name == DictionaryCatalog.NoneName), Is.False);
    }

    [TestCase("de-DE")]
    [TestCase("en-AU")]
    [TestCase("en-CA")]
    [TestCase("en-GB")]
    [TestCase("en-US")]
    [TestCase("es-ES")]
    [TestCase("es-MX")]
    [TestCase("fr-FR")]
    [TestCase("it-IT")]
    [TestCase("nl-NL")]
    [TestCase("pl-PL")]
    [TestCase("ro-RO")]
    [TestCase("ru-RU")]
    public async Task EveryBundledLanguageLoadsTheOriginalParserAndActuallyChecksWords(string language)
    {
        using Profile profile = new();
        await using SpellCheckService service = new(Installed(), language, profile.UserFile);
        await service.Ready;
        SpellCheckResult result = await service.CheckAsync("codexunknownwordqqq");
        Assert.That(result.DictionaryName, Is.EqualTo(language));
        Assert.That(result.Misspellings.Select(span => span.Word), Is.EqualTo(new[] { "codexunknownwordqqq" }));
        Assert.That(File.Exists(profile.UserFile), Is.False, "Loading must not create a user dictionary.");
    }

    [TestCase("en-US", "walking walked cats")]
    [TestCase("fr-FR", "écoles cafés ordinateurs")]
    [TestCase("ru-RU", "привет слово кот")]
    public async Task RealWordsAccentsAndAffixesUseTheirSelectedDictionary(string language, string text)
    {
        using Profile profile = new();
        await using SpellCheckService service = new(Installed(), language, profile.UserFile);
        Assert.That((await service.CheckAsync(text)).Misspellings, Is.Empty);
    }

    [Test]
    public async Task CheckReturnsUtf16SpansAndSuggestionsAreOnlyComputedForTheRequestedWord()
    {
        using Profile profile = new();
        await using SpellCheckService service = new(Installed(), "en-US", profile.UserFile);
        const string text = "hello wrld\nhello codexunknownwordqqq";
        SpellCheckResult result = await service.CheckAsync(text);
        Assert.That(result.Text, Is.EqualTo(text));
        Assert.That(result.Misspellings, Is.EqualTo(new[] { new SpellingSpan(6, 4, "wrld"), new SpellingSpan(17, 19, "codexunknownwordqqq") }));
        foreach (SpellingSpan span in result.Misspellings) { Assert.That(text[span.Start..span.End], Is.EqualTo(span.Word)); }
        SpellSuggestionsResult suggestions = await service.SuggestAsync("wrld");
        Assert.That(suggestions.Word, Is.EqualTo("wrld"));
        Assert.That(suggestions.Suggestions.Length, Is.InRange(1, 5));
        Assert.That(suggestions.Suggestions, Does.Contain("world"));
        SpellSuggestionsResult correct = await service.SuggestAsync("hello");
        Assert.That(correct.Suggestions, Is.Empty, "No suggestions may leak from the previous request.");
        Assert.That(result.Misspellings.Length, Is.EqualTo(2), "The earlier result remains immutable.");
    }

    [Test]
    public async Task CapsLeadingDigitsAndDuplicateWordsMatchTheWindowsSubscriberContract()
    {
        using Profile profile = new();
        await using SpellCheckService service = new(Installed(), "en-US", profile.UserFile);
        SpellCheckResult result = await service.CheckAsync("WRLD 123qqq ab123cdqqq hello hello");
        Assert.That(result.Misspellings.Select(item => item.Word), Is.EqualTo(new[] { "ab123cdqqq" }));
    }

    [Test]
    public async Task NoneDoesNotReadOrCreateAnyCatalogueOrUserFile()
    {
        using Profile profile = new();
        string missing = Path.Combine(profile.Root, "missing-assets");
        await using SpellCheckService service = new(new(missing), "None", profile.UserFile);
        await service.Ready;
        Assert.That((await service.CheckAsync("wrongqqq")).Misspellings, Is.Empty);
        Assert.That((await service.SuggestAsync("wrongqqq")).Suggestions, Is.Empty);
        Assert.CatchAsync<InvalidOperationException>(async () => await service.AddWordAsync("wrongqqq"));
        Assert.That(Directory.Exists(missing), Is.False);
        Assert.That(Directory.Exists(Path.GetDirectoryName(profile.UserFile)), Is.False);
    }

    [Test]
    public async Task MissingAndCorruptResourcesRemainErrorsRatherThanBecomingNone()
    {
        using Profile profile = new();
        DictionaryCatalog catalog = profile.MinimalCatalog();
        await using (SpellCheckService missing = new(catalog, "not-installed", profile.UserFile))
        {
            Assert.CatchAsync<FileNotFoundException>(async () => await missing.Ready);
            Assert.CatchAsync<FileNotFoundException>(async () => await missing.CheckAsync("hello"));
            Assert.That(missing.IsEnabled, Is.True);
        }
        File.WriteAllText(Path.Combine(catalog.InstallationDirectory, "en-US.dic"), "not a NetSpell dictionary");
        await using SpellCheckService corrupt = new(catalog, null, profile.UserFile);
        Assert.CatchAsync<InvalidDataException>(async () => await corrupt.Ready);
        Assert.CatchAsync<InvalidDataException>(async () => await corrupt.CheckAsync("hello"));
    }

    [Test]
    public async Task TwoLanguagesHaveIndependentEnginesAndUserAddIsSharedOnlyWithinTheExplicitProfile()
    {
        using Profile profile = new(); using Profile other = new();
        await using SpellCheckService english = new(Installed(), "en-US", profile.UserFile);
        await using SpellCheckService french = new(Installed(), "fr-FR", profile.UserFile);
        await using SpellCheckService isolated = new(Installed(), "en-US", other.UserFile);
        SpellCheckResult[] original = await Task.WhenAll(english.CheckAsync("ordinateur"), french.CheckAsync("ordinateur"));
        Assert.That(original[0].Misspellings.Single().Word, Is.EqualTo("ordinateur"));
        Assert.That(original[1].Misspellings, Is.Empty);
        byte[] asset = File.ReadAllBytes(Installed().Resolve("en-US").FilePath);
        await Task.WhenAll(english.AddWordAsync("codexlearnedqqq"), french.AddWordAsync("codexsecondqqq"));
        Assert.That((await english.CheckAsync("codexlearnedqqq codexsecondqqq")).Misspellings, Is.Empty);
        Assert.That((await french.CheckAsync("codexlearnedqqq codexsecondqqq")).Misspellings, Is.Empty);
        Assert.That((await isolated.CheckAsync("codexlearnedqqq")).Misspellings.Single().Word, Is.EqualTo("codexlearnedqqq"));
        Assert.That(File.ReadAllLines(profile.UserFile), Is.EquivalentTo(new[] { "codexlearnedqqq", "codexsecondqqq" }));
        Assert.That(File.ReadAllBytes(Installed().Resolve("en-US").FilePath), Is.EqualTo(asset));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.UserFile)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task UserFileIoFailureIsVisibleAndDoesNotPoisonLaterJobsOrTouchAssets()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        byte[] before = File.ReadAllBytes(catalog.Resolve("en-US").FilePath);
        await using SpellCheckService service = new(catalog, "en-US", profile.UserFile);
        await service.Ready;
        Directory.CreateDirectory(profile.UserFile);
        Exception? error = null;
        try { await service.AddWordAsync("codexlearnedqqq"); } catch (Exception caught) { error = caught; }
        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(File.ReadAllBytes(catalog.Resolve("en-US").FilePath), Is.EqualTo(before));
        Directory.Delete(profile.UserFile);
        Assert.That(await service.AddWordAsync("codexlearnedqqq"), Is.True);
        Assert.That(await service.AddWordAsync("codexlearnedqqq"), Is.False);
        Assert.That((await service.CheckAsync("codexlearnedqqq")).Misspellings, Is.Empty);
    }

    [Test]
    public async Task InvalidUserUtf8IsAnErrorAndIsNeverRewrittenByLoading()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        Directory.CreateDirectory(Path.GetDirectoryName(profile.UserFile)!);
        byte[] invalid = [0xff, 0xff, 0xff]; File.WriteAllBytes(profile.UserFile, invalid);
        await using SpellCheckService service = new(catalog, "en-US", profile.UserFile);
        Assert.CatchAsync<DecoderFallbackException>(async () => await service.Ready);
        Assert.That(File.ReadAllBytes(profile.UserFile), Is.EqualTo(invalid));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ACancelledWaiterCannotAbandonTheRawLoaderAndCloseDrainsIt(bool closeWhileLoading)
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> rawReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SpellCheckService service = new(catalog, "en-US", profile.UserFile, (resource, _) =>
        {
            entered.TrySetResult(true); release.Wait();
            NetSpellEngine engine = NetSpellEngine.Load(resource, CancellationToken.None);
            rawReturned.TrySetResult(true); return engine;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using CancellationTokenSource consumer = new();
            Task<SpellCheckResult> pending = service.CheckAsync("wrld", consumer.Token);
            consumer.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(rawReturned.Task.IsCompleted, Is.False);
            Task? close = closeWhileLoading ? service.BeginClose() : null;
            if (close is not null) { Assert.That(close.IsCompleted, Is.False, "A raw loader ignoring cancellation is still owned."); }
            release.Set(); await rawReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (close is not null)
            {
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.CatchAsync<OperationCanceledException>(async () => await service.Ready);
                Assert.Throws<ObjectDisposedException>(() => service.CheckAsync("hello"));
            }
            else
            {
                await service.Ready;
                Assert.That((await service.CheckAsync("hello")).Misspellings, Is.Empty);
            }
        }
        finally { release.Set(); await service.DisposeAsync(); }
        Assert.That(service.CloseCompletion.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task CompletionWordsAreCaseSensitiveAppendOnlyAndBelongOnlyToTheirEditorEngine()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        const string first = "codexCompletionAlphaqqq", wrongCase = "codexcompletionalphaqqq", second = "codexCompletionBetaqqq";
        byte[] asset = File.ReadAllBytes(catalog.Resolve("en-US").FilePath);
        await using (SpellCheckService service = new(catalog, "en-US", profile.UserFile))
        await using (SpellCheckService other = new(catalog, "en-US", profile.UserFile))
        {
            Assert.That((await service.CheckAsync(first + " " + wrongCase)).Misspellings.Select(item => item.Word), Is.EqualTo(new[] { first, wrongCase }));
            await service.AddCompletionWordsAsync([first, first, "file name.cs", "Signed-off-by: ", ""]);
            await service.AddCompletionWordsAsync([second]);
            Assert.That((await service.CheckAsync(first + " " + wrongCase + " " + second)).Misspellings.Select(item => item.Word), Is.EqualTo(new[] { wrongCase }),
                "A later corpus appends to the original case-sensitive HashSet, rather than replacing or normalizing it.");
            Assert.That((await other.CheckAsync(first + " " + second)).Misspellings.Select(item => item.Word), Is.EqualTo(new[] { first, second }),
                "Sharing a profile must not share temporary completion exemptions between editor engines.");
            Assert.That(File.Exists(profile.UserFile), Is.False); Assert.That(Directory.Exists(Path.GetDirectoryName(profile.UserFile)), Is.False);
            Assert.That(File.ReadAllBytes(catalog.Resolve("en-US").FilePath), Is.EqualTo(asset));
        }
        await using SpellCheckService reopened = new(catalog, "en-US", profile.UserFile);
        Assert.That((await reopened.CheckAsync(first)).Misspellings.Single().Word, Is.EqualTo(first),
            "A new engine requires explicit corpus reinjection; completion words are never persisted.");
    }

    [Test]
    public async Task CompletionPublicationDoesNotReadOrRewriteTheExplicitUserFile()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        await using SpellCheckService service = new(catalog, "en-US", profile.UserFile);
        await service.Ready;
        Directory.CreateDirectory(Path.GetDirectoryName(profile.UserFile)!);
        byte[] invalid = [0xff, 0xff, 0xff]; File.WriteAllBytes(profile.UserFile, invalid);
        await service.AddCompletionWordsAsync(["codexcompletionqqq"]);
        Assert.That(File.ReadAllBytes(profile.UserFile), Is.EqualTo(invalid),
            "A pure in-memory publication must neither reload invalid user UTF-8 nor rewrite that file.");
        File.Delete(profile.UserFile);
        Assert.That((await service.CheckAsync("codexcompletionqqq")).Misspellings, Is.Empty);
        Assert.That(File.Exists(profile.UserFile), Is.False); Assert.That(Directory.GetFiles(Path.GetDirectoryName(profile.UserFile)!), Is.Empty);
    }

    [Test]
    public async Task NoneAcceptsTemporaryCompletionWordsWithoutLoadingAnyEngineAssetOrUserFile()
    {
        using Profile profile = new(); string missing = Path.Combine(profile.Root, "missing-assets");
        bool loaded = false;
        await using SpellCheckService service = new(new(missing), "None", profile.UserFile, (_, _) =>
        { loaded = true; throw new AssertionException("None must never invoke an engine loader."); });
        await service.AddCompletionWordsAsync(["codexcompletionqqq", "Reviewed-by: ", "file name.cs"]);
        Assert.That((await service.CheckAsync("codexcompletionqqq")).Misspellings, Is.Empty);
        Assert.That(loaded, Is.False); Assert.That(Directory.Exists(missing), Is.False);
        Assert.That(Directory.Exists(Path.GetDirectoryName(profile.UserFile)), Is.False);
    }

    [Test]
    public async Task CompletionWordsAreCapturedBeforeTheQueueAndPrecedeTheFollowingRealCheck()
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SpellCheckService service = new(catalog, "en-US", profile.UserFile, (resource, _) =>
        { entered.TrySetResult(true); release.Wait(); return NetSpellEngine.Load(resource, CancellationToken.None); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            List<string> words = ["codexcapturedqqq"];
            Task publishing = service.AddCompletionWordsAsync(words);
            words[0] = "codexmutatedqqq"; words.Clear();
            Task<SpellCheckResult> check = service.CheckAsync("codexcapturedqqq codexmutatedqqq");
            Assert.That(publishing.IsCompleted, Is.False); Assert.That(check.IsCompleted, Is.False);
            release.Set(); await publishing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That((await check.WaitAsync(TimeSpan.FromSeconds(5))).Misspellings.Select(item => item.Word), Is.EqualTo(new[] { "codexmutatedqqq" }),
                "The original captured words must be installed before the later serialized check, despite caller mutation.");
            Assert.That(File.Exists(profile.UserFile), Is.False);
        }
        finally { release.Set(); await service.DisposeAsync(); }
        Assert.That(service.CloseCompletion.IsCompletedSuccessfully, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ACanceledCompletionWaiterCannotAbandonTheOwnedLoaderOrTheQueuedOrder(bool closeWhileLoading)
    {
        using Profile profile = new(); DictionaryCatalog catalog = profile.MinimalCatalog();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> rawReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SpellCheckService service = new(catalog, "en-US", profile.UserFile, (resource, _) =>
        {
            entered.TrySetResult(true); release.Wait();
            NetSpellEngine engine = NetSpellEngine.Load(resource, CancellationToken.None);
            rawReturned.TrySetResult(true); return engine;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using CancellationTokenSource consumer = new();
            Task publishing = service.AddCompletionWordsAsync(["codexcanceledqqq"], consumer.Token);
            Task<SpellCheckResult> following = service.CheckAsync("codexcanceledqqq");
            consumer.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await publishing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(rawReturned.Task.IsCompleted, Is.False); Assert.That(following.IsCompleted, Is.False);
            Task? closing = closeWhileLoading ? service.BeginClose() : null;
            if (closing is not null)
            {
                Assert.That(closing.IsCompleted, Is.False, "A canceled waiter cannot pretend the real gated loader and queued jobs are drained.");
                Assert.Throws<ObjectDisposedException>(() => service.AddCompletionWordsAsync(["latewordqqq"]));
            }
            release.Set(); await rawReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (closing is not null)
            {
                Assert.CatchAsync<OperationCanceledException>(async () => await following.WaitAsync(TimeSpan.FromSeconds(5)));
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(service.CloseCompletion.IsCompletedSuccessfully, Is.True);
            }
            else
            {
                Assert.That((await following.WaitAsync(TimeSpan.FromSeconds(5))).Misspellings.Single().Word, Is.EqualTo("codexcanceledqqq"),
                    "Cancellation before the queued publication starts must not install its words or poison the following check.");
                await service.AddCompletionWordsAsync(["codexlaterqqq"]);
                Assert.That((await service.CheckAsync("codexlaterqqq")).Misspellings, Is.Empty);
            }
            Assert.That(File.Exists(profile.UserFile), Is.False);
        }
        finally { release.Set(); await service.DisposeAsync(); }
    }

    [Test]
    public async Task APrecanceledCompletionPublicationDoesNotEnumerateItsInputOrAdmitWork()
    {
        using Profile profile = new(); bool enumerated = false;
        await using SpellCheckService service = new(profile.MinimalCatalog(), "en-US", profile.UserFile);
        using CancellationTokenSource consumer = new(); consumer.Cancel();
        IEnumerable<string> Words() { enumerated = true; yield return "codexcanceledqqq"; }
        Assert.Throws<OperationCanceledException>(() => service.AddCompletionWordsAsync(Words(), consumer.Token));
        Assert.That(enumerated, Is.False);
        Assert.That((await service.CheckAsync("codexcanceledqqq")).Misspellings.Single().Word, Is.EqualTo("codexcanceledqqq"));
        Assert.That(File.Exists(profile.UserFile), Is.False);
    }

    private static DictionaryCatalog Installed() => DictionaryCatalog.FromApplicationDirectory(AppContext.BaseDirectory);

    private static readonly IReadOnlyDictionary<string, string> ResourceHashes = new Dictionary<string, string>
    {
        ["de-DE"] = "dbbf60267a80d192c159914303c9179653b94c1392691c9d3a517136d893f089",
        ["en-AU"] = "3e464a3125c9735706eaee671ae62086cd21c3f4b0570d6bdef01f5d6758a8c7",
        ["en-CA"] = "4b8569291cbb721a549ab898f394196d6fc63b192b4462b889e7f09e0754f5e2",
        ["en-GB"] = "0a716fddb1ee999971bd18c1160262842efc007813cbd161e57ebe05c5cd8a34",
        ["en-US"] = "2600c2723bbd0147769b5ac8f241e7394dfca7312ca76f03d505b3c61e0a7c65",
        ["es-ES"] = "266c40dd938159b129c633340ede444c5621382c153d6cdda87cc3b32af33afa",
        ["es-MX"] = "fe992fc7f0495bcb5e2c2a82be751dfd4e482800bd55e34d750730bb5797bc99",
        ["fr-FR"] = "82938f5b92d642b8693d16cb1a1d888ac8c28068ecfd8b826be50974ff70ec6c",
        ["it-IT"] = "c7d31a69aed3a07105ca7f9ecb2a917c37eb39dc05737383227277d425cc2b5e",
        ["nl-NL"] = "1dde0e23addb58316f7b7df529eb5577a8c3b8b1060e149869a9417078bfede4",
        ["pl-PL"] = "5f4499980b30dfeaaa382bb1c6375c13d69a3de35f7efcad31ca1b810b728ca8",
        ["ro-RO"] = "ca886b6fc05b3e97e99b06b5b93d6bc5d1da74d0c726328bd285c736462c185a",
        ["ru-RU"] = "3e02ed6834928d0941d050eb5976126e4d250aaa9f8fa98f3b4043e19fe9bd13"
    };

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ge-spelling-" + Guid.NewGuid().ToString("N"));
        internal string UserFile => Path.Combine(Root, "profile", "NetSpell", "user.dic");
        internal Profile() { Directory.CreateDirectory(Root); }
        internal DictionaryCatalog MinimalCatalog()
        {
            string path = Path.Combine(Root, "Dictionaries"); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "en-US.dic"), "[Copyright]\nTiny functional test fixture\n[Try]\nabcdefghijklmnopqrstuvwxyz\n[Suffix]\nS Y 1\nS 0 s .\n[Words]\nhello\nworld\ncat/S\n", new UTF8Encoding(false));
            return new(path);
        }
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
