using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionQuickSearchTests
{
    [AvaloniaTest]
    [TestCase("subject")]
    [TestCase("body")]
    [TestCase("author")]
    [TestCase("local")]
    [TestCase("remote")]
    [TestCase("annotated-tag")]
    public async Task TypingSearchesTheAlreadyLoadedMessagesAuthorsAndFullReferencesEvenWhenBadgesAreHidden(string field)
    {
        await using Scope scope = await Scope.OpenAsync();
        (string term, string target) = field switch
        {
            "subject" => ("subjectbeacon", scope.BodyCommit),
            "body" => ("bodyonlyneedle", scope.BodyCommit),
            "author" => ("authorneedle", scope.AuthorCommit),
            "local" => ("localonlyneedle", scope.OlderOrbit),
            "remote" => ("remoteonlyneedle", scope.AuthorCommit),
            _ => ("annotatedonlyneedle", scope.BodyCommit)
        };
        await scope.ClickCommitAsync(scope.Head);
        Assert.That(scope.History.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "RevisionReference")
            .Select(border => border.Tag).OfType<GitReference>().Any(reference => reference.IsTag || reference.IsRemote), Is.False);
        if (field == "annotated-tag")
        {
            GitReference tag = (await scope.Git.Repository.GetReferencesAsync()).Single(reference => reference.Name == "refs/tags/annotatedonlyneedle");
            Assert.That(tag.Hash, Is.Not.EqualTo(target), "The fixture must contain a real annotated-tag object, not a lightweight tag.");
            Assert.That(tag.RevisionHash, Is.EqualTo(target));
        }
        GitCommandLog.Clear();

        scope.Type(term);
        await scope.IdleAsync();

        Assert.That(scope.SelectedHash, Is.EqualTo(target));
        Assert.That(scope.History.SelectedItems!.Count, Is.EqualTo(1));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True, "The very first typed character must work from the clicked row.");
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True);
        Assert.That(scope.NoticeText.Text, Does.EndWith(term));
        scope.AssertNoHistoryReload();
        Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase("en-US", "ipek")]
    [TestCase("tr-TR", "ıpek")]
    public async Task TypedCharactersAndAuthorMatchingUseTheCurrentCulture(string culture, string lowered)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using Scope scope = await Scope.OpenAsync();
            await scope.ClickCommitAsync(scope.Head);
            scope.Type("IPEK"); await scope.IdleAsync();
            Assert.That(scope.SelectedHash, Is.EqualTo(scope.AuthorCommit));
            Assert.That(scope.NoticeText.Text, Does.EndWith(lowered));
            scope.AssertNoHistoryReload();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaTest]
    public async Task TheCurrentMatchDoesNotReselectAndAltArrowsWrapTheLastTermAfterEscape()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.OlderOrbit);
        int selections = 0;
        scope.History.SelectionChanged += (_, _) => selections++;
        GitCommandLog.Clear(); scope.Type("orb"); scope.Type("it"); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(scope.OlderOrbit));
        Assert.That(selections, Is.Zero, "Extending a search which still matches the current row must not start another selection read.");
        Assert.That(GitCommandLog.Snapshot(), Is.Empty);

        string[] matches = scope.History.Items.OfType<GitCommit>().Where(commit => commit.Subject.Contains("Orbit", StringComparison.Ordinal)).Select(commit => commit.Hash).ToArray();
        Assert.That(matches, Has.Length.EqualTo(2));
        int index = Array.IndexOf(matches, scope.OlderOrbit);
        foreach (int delta in new[] { 1, 1, -1, -1 })
        {
            Press(scope.Main, delta > 0 ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp, RawInputModifiers.Alt);
            await scope.IdleAsync(); index = (index + delta + matches.Length) % matches.Length;
            Assert.That(scope.SelectedHash, Is.EqualTo(matches[index]));
            Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        }
        Press(scope.Main, PhysicalKey.Escape);
        Assert.That(scope.Notice.IsVisible, Is.False);
        Press(scope.Main, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(matches[(index + 1) % matches.Length]));
        Assert.That(scope.NoticeText.Text, Does.EndWith("orbit"));
        Assert.That(scope.Main.IsVisible, Is.True);
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    public async Task ARealMultipleSelectionBecomesOneMatchAndNoMatchLeavesThatSelectionIntact()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.Head);
        scope.ClickCommit(scope.OlderOrbit, RawInputModifiers.Control); await scope.IdleAsync();
        Assert.That(scope.History.SelectedItems!.Count, Is.EqualTo(2));
        scope.Type("bodyonlyneedle"); await scope.IdleAsync();
        Assert.That(scope.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { scope.BodyCommit }));
        IBrush? found = scope.NoticeText.Foreground;
        scope.Type("not-present-anywhere"); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(scope.BodyCommit));
        Assert.That(scope.History.SelectedItems!.Count, Is.EqualTo(1));
        Assert.That(scope.NoticeText.Foreground, Is.Not.EqualTo(found), "No-match feedback must be visible rather than silently retaining the successful state.");
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    public async Task OnlyAShaPrefixLongerThanTwoMatchesAndAnInternalShaFragmentDoesNot()
    {
        await using Scope scope = await Scope.OpenAsync();
        GitCommit[] commits = scope.History.Items.OfType<GitCommit>().ToArray();
        IReadOnlyList<GitReference> references = await scope.Git.Repository.GetReferencesAsync();
        GitCommit target = commits.First(candidate => commits.All(commit =>
                !(commit.Subject + "\n" + commit.Body + "\n" + commit.AuthorName).Contains(candidate.Hash[..2], StringComparison.OrdinalIgnoreCase))
            && references.All(reference => !reference.ShortName.Contains(candidate.Hash[..2], StringComparison.OrdinalIgnoreCase))
            && candidate.Hash[..3] is not ("111" or "222")
            && commits.Count(commit => commit.Hash.StartsWith(candidate.Hash[..3], StringComparison.Ordinal)) == 1);
        string prefix = target.Hash[..3];
        string other = commits.First(commit => commit.Hash != target.Hash && !commit.Hash.StartsWith(prefix, StringComparison.Ordinal)).Hash;
        await scope.ClickCommitAsync(other);
        // Isolate the SHA-length rule: the first character alone can match
        // a message, author or reference before the full two-character term fails.
        scope.Main.KeyTextInput(prefix[..2]); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(other), "Two hex characters are not a SHA search.");
        scope.Type(prefix[2..]); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(target.Hash));
        Press(scope.Main, PhysicalKey.Escape);
        string fragment = Enumerable.Range(3, 25).Select(offset => target.Hash.Substring(offset, 8))
            .First(value => scope.History.Items.OfType<GitCommit>().All(commit => !commit.Hash.StartsWith(value, StringComparison.Ordinal)));
        await scope.ClickCommitAsync(other); await PasteAsync(scope, fragment);
        Assert.That(scope.SelectedHash, Is.EqualTo(other), "SHA matching is StartsWith, never Contains.");
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True);
        Press(scope.Main, PhysicalKey.Escape); await PasteAsync(scope, "refs/heads");
        Assert.That(scope.SelectedHash, Is.EqualTo(other), "Windows searches displayed ref names, not their refs/heads namespace prefix.");
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    public async Task BackspaceEditsLongerTermsButAtOneCharacterHidesAndKeepsTheLastSearch()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.Head); scope.Type("or"); await scope.IdleAsync();
        Press(scope.Main, PhysicalKey.Backspace); await scope.IdleAsync();
        Assert.That(scope.NoticeText.Text, Does.EndWith("o"));
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True);
        string selected = scope.SelectedHash;
        Press(scope.Main, PhysicalKey.Backspace);
        Assert.That(scope.Notice.IsVisible, Is.False);
        Assert.That(scope.SelectedHash, Is.EqualTo(selected));
        Press(scope.Main, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await scope.IdleAsync();
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True);
        Assert.That(scope.NoticeText.Text, Does.EndWith("o"));
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    public async Task TheRealFourSecondTimeoutHidesTheNoticeButAltNextStillUsesTheLastTerm()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.OlderOrbit); scope.Type("orbit"); await scope.IdleAsync();
        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True);
        await Until(() => !scope.Notice.IsVisible, TimeSpan.FromSeconds(7));
        Assert.That(elapsed.Elapsed, Is.GreaterThan(TimeSpan.FromSeconds(3.4)), "The original timeout is 4000 ms, not an immediate idle callback.");
        Assert.That(scope.SelectedHash, Is.EqualTo(scope.OlderOrbit));
        Press(scope.Main, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await scope.IdleAsync();
        Assert.That(scope.SelectedHash, Is.EqualTo(scope.Head));
        Assert.That(scope.NoticeText.Text, Does.EndWith("orbit"));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr-FR", false)]
    [TestCase("fr-FR", true)]
    public async Task TheOriginalHelpAndTheSuccessFailureNoticeRenderInBothThemesAndFrench(string culture, bool dark)
    {
        await using Scope scope = await Scope.OpenAsync(culture, dark);
        await scope.ClickCommitAsync(scope.Head); scope.Type("bodyonlyneedle"); await scope.IdleAsync();
        Assert.That(scope.NoticeText.Text, Does.StartWith(culture == "en" ? "Searching for: " : "Rechercher :"));
        Rect notice = Bounds(scope.Main, scope.Notice), history = Bounds(scope.Main, scope.History);
        Rect artificialRows = Bounds(scope.Main, Field<Control>(scope.Main, "WorkingRevisions"));
        Assert.That(notice.Width, Is.GreaterThan(80)); Assert.That(notice.Height, Is.GreaterThan(10));
        Assert.That(notice.X, Is.InRange(Math.Min(history.Left, artificialRows.Left), history.Right));
        Assert.That(notice.Y, Is.InRange(Math.Min(history.Top, artificialRows.Top), history.Bottom));
        Color foreground = ((ISolidColorBrush)scope.NoticeText.Foreground!).Color;
        Capture(scope.Main, $"quick-search-found-{culture}-{dark}.png");
        scope.Type("unmatched"); await scope.IdleAsync();
        Assert.That(((ISolidColorBrush)scope.NoticeText.Foreground!).Color, Is.Not.EqualTo(foreground));
        Capture(scope.Main, $"quick-search-no-match-{culture}-{dark}.png");
        Press(scope.Main, PhysicalKey.Escape);
        object? source = scope.History.ItemsSource; string selected = scope.SelectedHash;
        MenuItem root = Field<MenuItem>(scope.Main, "NavigateMenu"); PushProcessTestDriver.Pointer(root); Pump(scope.Main);
        MenuItem command = root.Items.OfType<MenuItem>().Single(item => item.Name == "NavigateQuickSearch");
        PushProcessTestDriver.Pointer(command);
        await Until(() => scope.Main.OwnedWindows.Any(window => window.Name == "RevisionQuickSearchHelp"));
        Window help = scope.Main.OwnedWindows.Single(window => window.Name == "RevisionQuickSearchHelp"); Pump(help);
        Assert.That(Field<TextBlock>(help, "RevisionQuickSearchHelpText").Text,
            Is.EqualTo(culture == "en" ? "Start typing in revision grid to start quick search." : "Commencez à taper dans la grille pour démarrer la recherche rapide."));
        Capture(help, $"quick-search-help-{culture}-{dark}.png");
        PushProcessTestDriver.Pointer(help.GetVisualDescendants().OfType<Button>().Single());
        await Until(() => !help.IsVisible);
        Assert.That(scope.History.ItemsSource, Is.SameAs(source)); Assert.That(scope.SelectedHash, Is.EqualTo(selected));
        Assert.That(scope.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    public async Task TypingInTheHistoryFilterOrReadOnlyDiffDoesNotStartOrExtendGridSearch()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.Head); string selected = scope.SelectedHash;
        TextBox filter = Field<TextBox>(scope.Main, "RevisionQuickFilter");
        PushProcessTestDriver.Pointer(filter); scope.Main.KeyTextInput("unapplied literal filter"); Pump(scope.Main);
        Assert.That(filter.Text, Is.EqualTo("unapplied literal filter"));
        Assert.That(scope.Notice.IsVisible, Is.False); Assert.That(scope.SelectedHash, Is.EqualTo(selected));
        DiffView diff = Field<DiffView>(scope.Main, "CommitDiff");
        Control editor = diff.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "DiffTextEditor");
        PushProcessTestDriver.Pointer(editor); scope.Main.KeyTextInput("bodyonlyneedle"); Pump(scope.Main);
        Assert.That(scope.Notice.IsVisible, Is.False); Assert.That(scope.SelectedHash, Is.EqualTo(selected));
        Assert.That(filter.Text, Is.EqualTo("unapplied literal filter"));
        scope.AssertNoHistoryReload();
        Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    public async Task CtrlVPastesTheExactClipboardTextIntoGridSearchWithoutTouchingTheFilter()
    {
        await using Scope scope = await Scope.OpenAsync();
        int calls = 0;
        scope.Main.QuickSearchClipboardReader = _ => { calls++; return Task.FromResult<string?>("BODYONLYNEEDLE"); };
        await scope.ClickCommitAsync(scope.Head);
        Press(scope.Main, PhysicalKey.V, RawInputModifiers.Control);
        await scope.Main.QuickSearchCompletion; await scope.IdleAsync();
        Assert.That(calls, Is.EqualTo(1)); Assert.That(scope.SelectedHash, Is.EqualTo(scope.BodyCommit));
        Assert.That(scope.NoticeText.Text, Does.EndWith("BODYONLYNEEDLE"), "Windows lowercases typed characters but keeps the pasted string literal.");
        Assert.That(Field<TextBox>(scope.Main, "RevisionQuickFilter").Text ?? "", Is.Empty);
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True); scope.AssertNoHistoryReload();
    }

    [AvaloniaTest]
    [TestCase("typing")]
    [TestCase("manual-selection")]
    [TestCase("reopen")]
    [TestCase("close")]
    public async Task AClipboardResponseWhichIgnoresCancellationCannotPublishAfterTheInputOrWindowContextChanged(string change)
    {
        await using Scope scope = await Scope.OpenAsync();
        TaskCompletionSource<string?> clipboard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        scope.Main.QuickSearchClipboardReader = _ => { started = true; return clipboard.Task; };
        await scope.ClickCommitAsync(scope.Head);
        Press(scope.Main, PhysicalKey.V, RawInputModifiers.Control); await Until(() => started);
        Task pending = scope.Main.QuickSearchCompletion;
        try
        {
            if (change == "typing") { scope.Type("authorneedle"); await scope.IdleAsync(); }
            else if (change == "manual-selection") { await scope.ClickCommitAsync(scope.AuthorCommit); }
            else if (change == "reopen") { await scope.Main.OpenRepositoryAsync(scope.Git.Repository.Path); await scope.IdleAsync(); }
            else { scope.Main.Close(); }
            string? expected = (scope.History.SelectedItem as GitCommit)?.Hash;
            object? source = scope.History.ItemsSource;
            clipboard.SetResult("bodyonlyneedle"); await Drain(pending); await Drain(scope.Main.QuickSearchCompletion);
            if (change == "close")
            {
                await Until(() => !scope.Main.IsVisible && scope.Main.WindowWorkCompletion.IsCompleted);
                Assert.That(scope.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
            }
            else
            {
                await scope.IdleAsync(); Assert.That(scope.SelectedHash, Is.EqualTo(expected));
                Assert.That(scope.History.ItemsSource, Is.SameAs(source));
                Assert.That(scope.SelectedHash, Is.Not.EqualTo(scope.BodyCommit));
                if (change == "typing") { Assert.That(scope.NoticeText.Text, Does.EndWith("authorneedle")); }
            }
            Assert.That(scope.Main.LastException, Is.Null);
        }
        finally { clipboard.TrySetResult(null); await Drain(pending); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task QuickTypingCancelsTheRealObsoleteDiffAndOwnerCloseDrainsItWithoutRebindingTheHistory(bool close)
    {
        await using Scope scope = await Scope.OpenAsync();
        using DiffGate gate = new(scope.Git, scope.BodyCommit);
        await scope.ClickCommitAsync(scope.Head); GitCommandLog.Clear();
        scope.Type("bodyonlyneedle"); await Until(() => gate.Started);
        int pid = gate.Pid; Task oldWork = scope.Main.WindowWorkCompletion;
        Assert.That(scope.History.IsEffectivelyEnabled && scope.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(scope.History.ItemsSource, Is.SameAs(scope.Source));
        if (close)
        {
            scope.Main.Close();
            await Until(() => !scope.Main.IsVisible && oldWork.IsCompleted && scope.Main.WindowWorkCompletion.IsCompleted);
            await Drain(oldWork);
        }
        else
        {
            Press(scope.Main, PhysicalKey.Escape); scope.Type("authorneedle"); await scope.IdleAsync();
            Assert.That(scope.SelectedHash, Is.EqualTo(scope.AuthorCommit));
            Assert.That(Field<DiffView>(scope.Main, "CommitDiff").DocumentIdentity, Does.Contain(scope.AuthorCommit));
            Assert.That(Field<DiffView>(scope.Main, "CommitDiff").Text, Does.Contain("+author content"));
            Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
            Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Cancelled), Is.True);
        }
        Assert.That(Alive(pid), Is.False, "The real old Git process must have ended before the operation is considered drained.");
        Assert.That(scope.Main.LastException, Is.Null); scope.AssertNoHistoryReload();
        Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ArtificialSearchIdsRequireThreeCharactersAndSelectTypedRowsEvenWhenCleanOrUnborn(bool unborn, bool staged)
    {
        await using Scope scope = await Scope.OpenAsync(unborn: unborn, fileHistory: true);
        Button first = Field<Button>(scope.Main, staged ? "WorkingDirectoryRevision" : "IndexRevision");
        PushProcessTestDriver.Pointer(first); await scope.IdleAsync();
        string term = staged ? "222" : "111";
        GitCompareRevisionKind target = staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree;
        object? selected = scope.History.SelectedItem;
        scope.Type(term[..2]); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.Not.EqualTo(target), "The SHA shortcut threshold also applies to artificial IDs.");
        Assert.That(scope.History.SelectedItem, Is.SameAs(selected));
        scope.Type(term[2..]); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(target));
        Assert.That(Field<Button>(scope.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision").IsKeyboardFocusWithin, Is.True);
        Assert.That(scope.History.SelectedItem, Is.Null);
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(scope.History.Items.Cast<object>(), Is.All.TypeOf<GitCommit>());
        Assert.That(scope.History.Items.OfType<GitCommit>().Any(commit => commit.Hash is "1111111111111111111111111111111111111111" or "2222222222222222222222222222222222222222"), Is.False);
        Assert.That(scope.Notice.IsEffectivelyVisible, Is.True); Assert.That(scope.NoticeText.Text, Does.EndWith(term));
        Press(scope.Main, PhysicalKey.Escape);
        Press(scope.Main, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(target), "Alt+Next from an artificial button must wrap the same last term.");
        Assert.That(scope.Main.LastException, Is.Null, "A synthetic search ID must never be sent to Git as a real commit.");
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase("en")]
    [TestCase("fr-FR")]
    public async Task ArtificialTranslatedNamesAndTheConfiguredUserAreSearchableFromTheVirtualButtons(string culture)
    {
        await using Scope scope = await Scope.OpenAsync(culture);
        await scope.Git.GitAsync("config", "user.name", "ArtificialOnlyUser");
        await scope.Main.OpenRepositoryAsync(scope.Git.Repository.Path); await scope.IdleAsync();
        object? source = scope.History.ItemsSource;
        Button working = Field<Button>(scope.Main, "WorkingDirectoryRevision"), index = Field<Button>(scope.Main, "IndexRevision");
        Assert.That(((TextBlock)working.Content!).Text, Is.EqualTo(culture == "en" ? "Working directory" : "Répertoire de travail"));
        Assert.That(((TextBlock)index.Content!).Text, Is.EqualTo(culture == "en" ? "Commit index" : "Index du commit"));
        PushProcessTestDriver.Pointer(index); await scope.IdleAsync();
        scope.Type(culture == "en" ? "working directory" : "répertoire de travail"); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(working.IsKeyboardFocusWithin, Is.True);
        Press(scope.Main, PhysicalKey.Escape);
        scope.Type(culture == "en" ? "commit index" : "index du commit"); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Press(scope.Main, PhysicalKey.Escape); scope.Type("artificialonlyuser"); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.Index), "Typing tests the current artificial match first.");
        Press(scope.Main, PhysicalKey.ArrowDown, RawInputModifiers.Alt); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(scope.History.ItemsSource, Is.SameAs(source));
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"), "Ordinary Browse retains its actual working-files workflow.");
        Assert.That(scope.Main.LastException, Is.Null);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ArtificialDestinationsAreExcludedWhenTheirRowsAreHiddenOrTheRepositoryIsBare(bool bare)
    {
        await using Scope scope = await Scope.OpenAsync(showArtificial: bare, bare: bare);
        await scope.ClickCommitAsync(scope.Head);
        Assert.That(Field<Control>(scope.Main, "WorkingRevisions").IsEffectivelyVisible, Is.False);
        foreach (string term in new[] { "111", "222", "working directory", "commit index" })
        {
            await scope.ClickCommitAsync(scope.Head);
            GitCommit? realPrefixMatch = term is "111" or "222"
                ? scope.History.Items.OfType<GitCommit>().FirstOrDefault(commit => commit.Hash.StartsWith(term, StringComparison.Ordinal)) : null;
            string expected = scope.Head.StartsWith(term, StringComparison.Ordinal) ? scope.Head : realPrefixMatch?.Hash ?? scope.Head;
            await PasteAsync(scope, term);
            Assert.That(scope.SelectedHash, Is.EqualTo(expected), "Test the complete term atomically: intermediate prefixes may legitimately select another real commit.");
            Assert.That(ArtificialSelection(scope.Main), Is.Null);
            Press(scope.Main, PhysicalKey.Escape);
        }
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReturningFromOrdinaryBrowseArtificialSearchRestoresThePriorCommitPanelEvenForTheSameUniqueRealSelection(bool sameCommit)
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(sameCommit ? scope.AuthorCommit : scope.Head);
        PushProcessTestDriver.Pointer(Field<TabItem>(scope.Main, "DiffTab")); await scope.IdleAsync();
        Press(scope.Main, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        string before = scope.SelectedHash; object? source = scope.History.ItemsSource;
        scope.Type("111"); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.WorkingTree));
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        Assert.That(scope.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { before }),
            "Ordinary Browse keeps its actual unique real selection while the virtual action shows working files.");

        Press(scope.Main, PhysicalKey.Escape); scope.Type("authorneedle"); await scope.IdleAsync();

        Assert.That(scope.SelectedHash, Is.EqualTo(scope.AuthorCommit));
        Assert.That(ArtificialSelection(scope.Main), Is.Null);
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(Field<DiffView>(scope.Main, "CommitDiff").DocumentIdentity, Does.Contain(scope.AuthorCommit));
        Assert.That(Field<DiffView>(scope.Main, "CommitDiff").Text, Does.Contain("+author content"));
        Assert.That(Field<CommitInformationView>(scope.Main, "CommitInformation").Text, Does.Contain(scope.AuthorCommit));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(scope.History.ItemsSource, Is.SameAs(source));
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    public async Task AManuallyChosenCommitPanelIsNotReplacedWhenQuickSearchReturnsFromAVirtualRow()
    {
        await using Scope scope = await Scope.OpenAsync();
        await scope.ClickCommitAsync(scope.Head);
        PushProcessTestDriver.Pointer(Field<TabItem>(scope.Main, "DiffTab")); await scope.IdleAsync();
        Press(scope.Main, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.Type("111"); await scope.IdleAsync();
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        PushProcessTestDriver.Pointer(Field<TabItem>(scope.Main, "FileTreeTab")); await scope.IdleAsync();
        Press(scope.Main, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.Type("authorneedle"); await scope.IdleAsync();

        Assert.That(scope.SelectedHash, Is.EqualTo(scope.AuthorCommit));
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("FileTreeTab"),
            "Restore the saved panel only while the automatically selected ChangesTab is still active.");
        Assert.That(Field<FileContentView>(scope.Main, "CommitFileContent").TextView.Text, Is.EqualTo("author content\n"));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    public async Task MultilineClipboardSearchUsesTheExactNativeMessageRatherThanAFoldedSubjectReconstruction()
    {
        await using Scope scope = await Scope.OpenAsync();
        string native = (await scope.Git.GitAsync("show", "--no-notes", "-s", "--format=%B", scope.BodyCommit)).StandardOutput;
        const string term = "SubjectBeacon\nmultiline header\n\nbodyonlyneedle";
        Assert.That(native, Does.Contain(term), "The fixture must preserve a real multiline first paragraph which %s folds.");
        GitCommit row = scope.History.Items.OfType<GitCommit>().Single(commit => commit.Hash == scope.BodyCommit);
        Assert.That(row.Subject + "\n\n" + row.Body, Does.Not.Contain(term));
        await scope.ClickCommitAsync(scope.Head); GitCommandLog.Clear();

        await PasteAsync(scope, term);

        Assert.That(scope.SelectedHash, Is.EqualTo(scope.BodyCommit));
        Assert.That(scope.NoticeText.Text, Does.EndWith(term));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task SearchingTheSameUniqueRealRowReducesItsInitialArtificialPairToTheActualCommitComparison(GitCompareRevisionKind first)
    {
        await using Scope scope = await Scope.OpenAsync(fileHistory: true, firstRevision: first);
        Assert.That(scope.History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { scope.AuthorCommit }));
        Assert.That(scope.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Does.Contain(scope.Head), "First and Selected must never bound graph traversal.");
        DiffView diff = Field<DiffView>(scope.Main, "CommitDiff");
        Assert.That(diff.DocumentIdentity, Does.Contain(first.ToString()).And.Contain(scope.AuthorCommit));
        Assert.That(diff.Text, Does.Contain("-newest orbit content").And.Contain("+author content"));
        Press(scope.Main, PhysicalKey.Digit1, RawInputModifiers.Control); await scope.IdleAsync();
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        // Focusing the grid does not reduce its two logical comparison endpoints.
        Assert.That(diff.DocumentIdentity, Does.Contain(first.ToString()));
        object? source = scope.History.ItemsSource; GitCommandLog.Clear();

        scope.Type("authorneedle"); await scope.IdleAsync();

        Assert.That(scope.SelectedHash, Is.EqualTo(scope.AuthorCommit));
        Assert.That(scope.History.SelectedItems!.Count, Is.EqualTo(1));
        Assert.That(diff.DocumentIdentity, Does.Contain(scope.AuthorCommit).And.Not.Contain(first.ToString()));
        Assert.That(diff.Text, Does.Contain("-older orbit content").And.Contain("+author content").And.Not.Contain("-newest orbit content"));
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(scope.History.ItemsSource, Is.SameAs(source));
        scope.AssertNoHistoryReload(); Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARealSpaceKeyExtendsTheSearchOnAnArtificialButtonWithoutClickingOrReloadingItsWorkflow(bool staged)
    {
        await using Scope scope = await Scope.OpenAsync();
        Button button = Field<Button>(scope.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision");
        PushProcessTestDriver.Pointer(button); await scope.IdleAsync();
        GitCompareRevisionKind target = staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree;
        string firstWord = staged ? "commit" : "working";
        scope.Type(firstWord); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(target));
        Assert.That(button.IsKeyboardFocusWithin, Is.True);
        int clicks = 0; button.Click += (_, _) => clicks++;
        object? source = scope.History.ItemsSource;
        GitCommandLog.Clear();

        scope.Main.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        scope.Main.KeyTextInput(" ");
        scope.Main.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await scope.IdleAsync();

        Assert.That(clicks, Is.Zero, "TextInput must remain available without Button's Space release invoking its workflow.");
        Assert.That(scope.NoticeText.Text, Does.EndWith(firstWord + " "));
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(target));
        Assert.That(button.IsKeyboardFocusWithin, Is.True);
        Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Extending the current artificial match must not reload its diff/status workflow.");
        scope.Type(staged ? "index" : "directory"); await scope.IdleAsync();
        Assert.That(scope.NoticeText.Text, Does.EndWith(staged ? "commit index" : "working directory"));
        Assert.That(scope.History.ItemsSource, Is.SameAs(source));
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("ChangesTab"));
        Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    [AvaloniaTest]
    public async Task FocusingTheEmptyUnbornGridKeepsTheSelectedIndexAsTheFirstArtificialAuthorMatch()
    {
        await using Scope scope = await Scope.OpenAsync(unborn: true, fileHistory: true);
        Button index = Field<Button>(scope.Main, "IndexRevision");
        PushProcessTestDriver.Pointer(index); await scope.IdleAsync();
        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.Index));
        Assert.That(scope.History.Items, Is.Empty);
        Press(scope.Main, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.That(scope.History.IsKeyboardFocusWithin, Is.True, "Use the actual Ctrl+1 shortcut, without a focus repair.");
        object? source = scope.History.ItemsSource; GitCommandLog.Clear();

        scope.Type("reference tester"); await scope.IdleAsync();

        Assert.That(ArtificialSelection(scope.Main)?.Kind, Is.EqualTo(GitCompareRevisionKind.Index), "Both artificial rows have this configured author; test the current Index before wrapping to WT.");
        Assert.That(index.IsKeyboardFocusWithin, Is.True);
        Assert.That(scope.History.Items, Is.Empty);
        Assert.That(scope.History.ItemsSource, Is.SameAs(source));
        Assert.That(((TabItem)Field<TabControl>(scope.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(GitCommandLog.Snapshot(), Is.Empty, "A current match must not switch endpoints and reload an empty comparison.");
        Assert.That(await scope.MaterialAsync(), Is.EqualTo(scope.Ready));
    }

    private sealed record Material(string Head, string Index, string Refs, string File);

    private sealed class Scope(GitFixture git, MainWindow main, string repositoryPath, string bodyCommit, string olderOrbit, string authorCommit, string head) : IAsyncDisposable
    {
        internal GitFixture Git => git;
        internal MainWindow Main => main;
        internal string BodyCommit => bodyCommit;
        internal string OlderOrbit => olderOrbit;
        internal string AuthorCommit => authorCommit;
        internal string Head => head;
        internal ListBox History => Field<ListBox>(main, "RevisionHistory");
        internal Control Notice => Field<Control>(main, "RevisionQuickSearchNotice");
        internal TextBlock NoticeText => Field<TextBlock>(main, "RevisionQuickSearchText");
        internal string SelectedHash => ((GitCommit)History.SelectedItem!).Hash;
        internal object? Source { get; private set; }
        internal Material Ready { get; private set; } = null!;

        internal static async Task<Scope> OpenAsync(string culture = "en", bool dark = false, bool unborn = false, bool showArtificial = true, bool bare = false, bool fileHistory = false,
            GitCompareRevisionKind? firstRevision = null)
        {
            GitFixture git = await GitFixture.CreateAsync(unborn); MainWindow? main = null;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
                async Task<string> Commit(string content, string subject, string? body = null, string? author = null)
                {
                    await File.WriteAllTextAsync(git.File, content + "\n"); await git.GitAsync("add", "--", "file.txt");
                    List<string> arguments = ["commit", "-m", subject];
                    if (body is not null) { arguments.AddRange(["-m", body]); }
                    if (author is not null) { arguments.AddRange(["--author", author + " <quick@example.com>"]); }
                    await git.GitAsync(arguments.ToArray()); return await git.Repository.ResolveRevisionAsync("HEAD");
                }
                string bodyCommit = "", olderOrbit = "", authorCommit = "", head = "";
                if (!unborn)
                {
                    bodyCommit = await Commit("body content", "SubjectBeacon\nmultiline header", "bodyonlyneedle with a real supplementary paragraph");
                    olderOrbit = await Commit("older orbit content", "Orbit older");
                    authorCommit = await Commit("author content", "Plain attribution", author: "IPEK AuthorNeedle");
                    head = await Commit("newest orbit content", "Orbit newest");
                    await git.GitAsync("branch", "localonlyneedle", olderOrbit);
                    await git.GitAsync("update-ref", "refs/remotes/origin/remoteonlyneedle", authorCommit);
                    await git.GitAsync("tag", "-a", "annotatedonlyneedle", "-m", "Annotated fixture", bodyCommit);
                }
                string repositoryPath = git.Repository.Path;
                if (bare) { repositoryPath = git.Output("bare.git"); await git.GitAsync("clone", "--bare", git.Repository.Path, repositoryPath); }
                new HistoryViewPreferencesStore().Save(new() { ShowRemoteBranches = false, ShowTags = false, ShowArtificialCommits = showArtificial });
                new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList));
                main = new(new BrowseRequest(PathFilter: fileHistory ? new("file.txt", BrowsePathKind.File) : null,
                    SelectedRevision: firstRevision is null ? null : new(authorCommit),
                    FirstRevision: firstRevision is null ? null : new("", firstRevision.Value)))
                    { Width = 1600, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                main.Show(); await main.OpenRepositoryAsync(repositoryPath);
                Scope result = new(git, main, repositoryPath, bodyCommit, olderOrbit, authorCommit, head);
                await result.IdleAsync(); result.Source = result.History.ItemsSource; result.Ready = await result.MaterialAsync(); GitCommandLog.Clear();
                return result;
            }
            catch
            {
                if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); await Drain(main.WindowWorkCompletion); }
                git.Dispose(); throw;
            }
        }

        internal void ClickCommit(string hash, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            GitCommit commit = History.Items.OfType<GitCommit>().Single(row => row.Hash == hash);
            History.ScrollIntoView(commit); Pump(main);
            Control row = (Control)History.ContainerFromItem(commit)!;
            Point point = row.TranslatePoint(new Point(Math.Min(200, row.Bounds.Width / 2), row.Bounds.Height / 2), main)!.Value;
            Assert.That((main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True, "The real commit row must receive the click.");
            main.MouseMove(point); main.MouseDown(point, MouseButton.Left, modifiers); main.MouseUp(point, MouseButton.Left, modifiers); Pump(main);
        }
        internal async Task ClickCommitAsync(string hash) { ClickCommit(hash); await IdleAsync(); Assert.That(History.IsKeyboardFocusWithin, Is.True); }
        internal void Type(string value) { foreach (char character in value) { main.KeyTextInput(character.ToString()); } Pump(main); }
        internal async Task IdleAsync()
        {
            await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            await Drain(main.WindowWorkCompletion); Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
        internal void AssertNoHistoryReload()
        {
            Assert.That(History.ItemsSource, Is.SameAs(Source));
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"),
                "Selection details may read their exact commit/file; incremental search may never reload the history or global repository status.");
        }
        internal async Task<Material> MaterialAsync()
        {
            GitRunner reader = new("/usr/bin/git");
            GitResult headResult = await reader.RunAsync(repositoryPath, ["--no-optional-locks", "rev-parse", "--verify", "HEAD"]);
            Assert.That(headResult.ExitCode, Is.AnyOf(0, 128));
            GitResult refs = await reader.RunAsync(repositoryPath, ["--no-optional-locks", "show-ref"]); Assert.That(refs.ExitCode, Is.AnyOf(0, 1));
            string index = Path.Combine(repositoryPath, File.Exists(Path.Combine(repositoryPath, "HEAD")) ? "index" : ".git/index");
            string path = Path.Combine(repositoryPath, "file.txt");
            return new(headResult.Success ? headResult.StandardOutput : "<unborn>", File.Exists(index) ? Convert.ToHexString(await File.ReadAllBytesAsync(index)) : "<no index>",
                refs.StandardOutput, File.Exists(path) ? await File.ReadAllTextAsync(path) : "<no worktree file>");
        }
        public async ValueTask DisposeAsync()
        {
            main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
            await Drain(main.WindowWorkCompletion); git.Dispose();
        }
    }

    private sealed class DiffGate : IDisposable
    {
        private readonly GitFixture _git;
        private readonly string? _path;
        private readonly string _release, _marker;
        internal bool Started => File.Exists(_marker);
        internal int Pid => int.Parse(File.ReadAllText(_marker), CultureInfo.InvariantCulture);
        internal DiffGate(GitFixture git, string hash)
        {
            _git = git; _path = Environment.GetEnvironmentVariable("PATH");
            string directory = git.Output("quick-search-diff-gate"); Directory.CreateDirectory(directory);
            _release = Path.Combine(directory, "release"); _marker = Path.Combine(directory, "started");
            string executable = Path.Combine(directory, "git");
            File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import os,sys,time
                from pathlib import Path
                a=sys.argv[1:]
                if {{JsonSerializer.Serialize(hash)}} in a and any(x in a for x in ('diff','diff-tree')) and not Path({{JsonSerializer.Serialize(_release)}}).exists():
                    Path({{JsonSerializer.Serialize(_marker)}}).write_text(str(os.getpid()))
                    while not Path({{JsonSerializer.Serialize(_release)}}).exists():time.sleep(.01)
                os.execv('/usr/bin/git',['git',*a])
                """);
            if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            git.SetEnvironment("PATH", directory + Path.PathSeparator + _path);
        }
        public void Dispose() { File.WriteAllText(_release, "released"); _git.SetEnvironment("PATH", _path); }
    }

    private static bool Alive(int pid)
    {
        try { string stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X'); }
        catch (IOException) { return false; }
    }
    private static GitCompareRevision? ArtificialSelection(MainWindow window)
        => (GitCompareRevision?)typeof(MainWindow).GetField("_quickSearchArtificialSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static async Task Drain(Task work) { try { await work; } catch (OperationCanceledException) { } }
    private static async Task PasteAsync(Scope scope, string text)
    {
        scope.Main.QuickSearchClipboardReader = _ => Task.FromResult<string?>(text);
        Press(scope.Main, PhysicalKey.V, RawInputModifiers.Control);
        await scope.Main.QuickSearchCompletion; await scope.IdleAsync();
        Assert.That(scope.NoticeText.Text, Does.EndWith(text), "The complete clipboard term must reach the real search surface in one operation.");
    }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Rect Bounds(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < (timeout ?? TimeSpan.FromSeconds(20))) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(condition(), Is.True, "The actual quick-search selection/read/window operation did not settle.");
    }
}
