using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Spelling;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitSpellingTests
{
    [AvaloniaTest]
    [TestCase("none", "none")]
    [TestCase("fr-FR", "fr-FR")]
    public async Task RepositoryLocalDictionaryOverridesDistributedAndGlobalWithoutRewritingEither(string local, string expected)
    {
        using GitFixture git = await GitFixture.CreateAsync();
        string distributed = git.PathInRepository("GitExtensions.settings");
        File.WriteAllText(distributed, "<dictionary><item><key><string>dictionary</string></key><value><string>en-GB</string></value></item></dictionary>");
        GitDictionarySettingsSnapshot snapshot = await git.Repository.ReadEffectiveDictionaryAsync("en-US");
        await git.Repository.WriteLocalDictionaryAsync(snapshot, local);
        byte[] distributedBefore = File.ReadAllBytes(distributed);
        await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        host.Editor.Text = "ordinateur"; await host.Checked(0);
        Assert.That(host.Service.DictionaryName, Is.EqualTo(expected));
        Assert.That(host.Controller.Misspellings, Is.Empty);
        Assert.That(host.Desktop.Load().Dictionary, Is.EqualTo("en-US"));
        Assert.That(File.ReadAllBytes(distributed), Is.EqualTo(distributedBefore));
        Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("en-US")).Source, Is.EqualTo(GitDictionarySettingSource.Local));
    }

    [AvaloniaTest]
    public async Task NoneDoesNotTouchMissingAssetsOrTheUserDictionaryAndMenuChoicePersistsLocallyAcrossEditors()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        string profile = git.Output("none-profile"), assets = git.Output("missing-assets");
        await using (Host none = new(git.Repository, profile, "none", new(assets)))
        {
            none.Editor.Text = "wrld"; await none.Ready(); await none.Controller.Completion.WaitAsync(Limit);
            Assert.That(none.Controller.Misspellings, Is.Empty); Assert.That(none.Controller.Notice.IsVisible, Is.False);
            Assert.That(Directory.Exists(assets), Is.False); Assert.That(Directory.Exists(Path.GetDirectoryName(none.Service.UserDictionaryFile)), Is.False);
        }
        await using (Host choice = new(git.Repository, profile, "en-US"))
        {
            await choice.OpenMenu(); Invoke(choice.MenuItem("CommitDictionary_none")); choice.Menu.Close();
            await Until(() => ServiceOrNull(choice.Controller)?.DictionaryName == "none");
            Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("en-US")).LocalDictionary, Is.EqualTo("none"));
            Assert.That(choice.Desktop.Load().Dictionary, Is.EqualTo("en-US"), "The Commit menu writes the repository-local setting.");
        }
        await using Host reopened = new(git.Repository, profile, "en-US");
        await reopened.Ready(); Assert.That(reopened.Service.DictionaryName, Is.EqualTo("none"));
    }

    [AvaloniaTest]
    public async Task ChoosingNoneInTheRealDictionaryMenuRemovesRenderedUnderlinesWithoutEditingTheNativeTextBox()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        TextBox editor = host.Editor;
        const string text = "hello wrld";
        await AvatarUiTestDriver.Edit(host.Window, editor, text); await host.Checked(1);
        Assert.That(host.Controller.Misspellings.Single().Word, Is.EqualTo("wrld"));
        editor.SelectionStart = 6; editor.SelectionEnd = 10;
        Control marks = host.View.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "SpellingUnderlines");
        Rect word;
        double scale = host.Window.RenderScaling;
        using (WriteableBitmap before = await AvatarUiTestDriver.Frame(host.Window))
        {
            Rect local = host.View.GetRangeRectangles(new(6, 4)).Single();
            word = new(marks.TranslatePoint(local.Position, host.Window)!.Value, local.Size);
            Assert.That(CountRedPixels(before, word, scale), Is.GreaterThan(0),
                "The baseline must contain an actual rendered red underline, not only a misspelling result.");
        }
        // Use the genuine context gesture and the populated dictionary MenuItem handler.
        await host.OpenMenu(position: 7);
        int caret = editor.CaretIndex, selectionStart = editor.SelectionStart, selectionEnd = editor.SelectionEnd;
        Assert.That(selectionStart, Is.EqualTo(6)); Assert.That(selectionEnd, Is.EqualTo(10));
        Assert.That(editor.CanUndo, Is.True);
        Invoke(host.MenuItem("CommitDictionary_none")); host.Menu.Close();
        await Until(() => ServiceOrNull(host.Controller)?.DictionaryName == "none");
        await host.Controller.Completion.WaitAsync(Limit);
        Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("en-US")).LocalDictionary, Is.EqualTo("none"));
        Assert.That(host.Controller.Misspellings, Is.Empty);
        using (WriteableBitmap after = await AvatarUiTestDriver.Frame(host.Window))
        {
            Rect local = host.View.GetRangeRectangles(new(6, 4)).Single();
            Assert.That(new Rect(marks.TranslatePoint(local.Position, host.Window)!.Value, local.Size), Is.EqualTo(word),
                "The same visible word must occupy the same captured pixels after choosing None.");
            Assert.That(host.Window.RenderScaling, Is.EqualTo(scale));
            Assert.That(CountRedPixels(after, word, scale), Is.Zero,
                "Choosing None must remove the actual red pixels even when the text has not changed.");
        }
        Assert.That(host.View.Editor, Is.SameAs(editor)); Assert.That(editor.Text, Is.EqualTo(text));
        Assert.That(editor.CaretIndex, Is.EqualTo(caret)); Assert.That(editor.SelectionStart, Is.EqualTo(selectionStart));
        Assert.That(editor.SelectionEnd, Is.EqualTo(selectionEnd)); Assert.That(editor.CanUndo, Is.True);
        editor.Undo(); Assert.That(editor.Text, Is.Null.Or.Empty,
            "The native undo entry created before the dictionary change must still be usable.");

        static int CountRedPixels(WriteableBitmap frame, Rect rectangle, double scale)
        {
            int left = (int)Math.Floor(rectangle.Left * scale) - 1, top = (int)Math.Floor(rectangle.Top * scale) - 1;
            int right = (int)Math.Ceiling(rectangle.Right * scale) + 1, bottom = (int)Math.Ceiling(rectangle.Bottom * scale) + 1;
            Assert.That(left, Is.GreaterThanOrEqualTo(0)); Assert.That(top, Is.GreaterThanOrEqualTo(0));
            Assert.That(right, Is.LessThanOrEqualTo(frame.PixelSize.Width)); Assert.That(bottom, Is.LessThanOrEqualTo(frame.PixelSize.Height));
            int count = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                Color pixel = AvatarUiTestDriver.Pixel(frame, x, y);
                if (pixel.A > 0 && pixel.R >= 150 && (int)pixel.R - pixel.G >= 80 && (int)pixel.R - pixel.B >= 80) { count++; }
            }
            return count;
        }
    }

    [AvaloniaTest]
    public async Task ActualSavedProfilePublicationChangesOnlyItsEditorsAndKeepsTheirNativeTextSelectionAndFocus()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await using Host first = new(git.Repository, git.Output("first-profile"), "en-US");
        await using Host other = new(git.Repository, git.Output("other-profile"), "en-US");
        first.Editor.Text = other.Editor.Text = "ordinateur";
        await first.Checked(1); await other.Checked(1);
        await AvatarUiTestDriver.Click(first.Window, first.Editor);
        first.Editor.SelectionStart = 1; first.Editor.SelectionEnd = 4;
        object? focus = first.Window.FocusManager!.GetFocusedElement();
        first.Desktop.Save(first.Desktop.Load() with { Dictionary = "fr-FR", Theme = "Dark", HistoryLimit = 713 });
        DesktopPreferences saved = first.Desktop.Load();
        CommitSpellingController.PublishSavedDesktop(first.Desktop.FilePath, saved);
        await Until(() => ServiceOrNull(first.Controller)?.DictionaryName == "fr-FR"); await first.Checked(0);
        Assert.That(other.Service.DictionaryName, Is.EqualTo("en-US")); Assert.That(other.Controller.Misspellings.Count, Is.EqualTo(1));
        Assert.That(first.Editor.Text, Is.EqualTo("ordinateur")); Assert.That(first.Editor.SelectionStart, Is.EqualTo(1)); Assert.That(first.Editor.SelectionEnd, Is.EqualTo(4));
        Assert.That(first.Window.FocusManager.GetFocusedElement(), Is.SameAs(focus));
        Assert.That(first.Desktop.Load().Theme, Is.EqualTo("Dark")); Assert.That(first.Desktop.Load().HistoryLimit, Is.EqualTo(713));
        Assert.That(first.Controller.Notice.IsVisible, Is.False, "Unavailable Git completion providers belong in the menu tooltip, not a permanent row.");
    }

    [AvaloniaTest]
    public async Task CheckingWaitsForTheDebounceAndAnObsoleteTextNeverRepopulatesItsSpans()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        await host.Ready(); Stopwatch elapsed = Stopwatch.StartNew();
        host.Editor.Text = "wrld";
        Assert.That(host.Controller.Misspellings, Is.Empty, "A text notification cannot run the engine synchronously.");
        await host.Checked(1); Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(230)));
        host.Editor.Text = "codexunknownwordqqq"; host.Editor.Text = "hello world";
        await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Controller.Misspellings, Is.Empty);
        host.Editor.Text = "wrld"; await host.Checked(1);
        Assert.That(host.Controller.Misspellings.Single().Word, Is.EqualTo("wrld"));
    }

    [AvaloniaTest]
    public async Task AnExplicitCheckPathAlsoChecksAThreeCharacterMisspelling()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        host.Editor.Text = "zzq"; await host.Checked(1);
        Assert.That(host.Controller.Misspellings.Single().Word, Is.EqualTo("zzq"));
    }

    [AvaloniaTest]
    [TestCase(4999, 1)]
    [TestCase(5000, 0)]
    public async Task TheWindowsLengthBoundaryChecks4999AndClears5000Characters(int length, int expected)
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        host.Editor.Text = "wrld"; await host.Checked(1);
        host.Editor.Text = "wrld" + new string(' ', length - 4);
        await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Editor.Text!.Length, Is.EqualTo(length)); Assert.That(host.Controller.Misspellings.Count, Is.EqualTo(expected));
        if (expected != 0) { Assert.That(host.Controller.Misspellings.Single().Start, Is.Zero); }
    }

    [AvaloniaTest]
    public async Task RealPresenterPreeditClearsTheCurrentSpansAndRechecksTheCommittedTextAfterComposition()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "wrld"); await host.Checked(1);
        AvatarUiTestDriver.Press(host.Window, PhysicalKey.Home);
        using (await AvatarUiTestDriver.Frame(host.Window)) { }
        TextPresenter presenter = host.Editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        TextInputMethodClientRequestedEventArgs request = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        host.Editor.RaiseEvent(request);
        TextInputMethodClient client = request.Client ?? throw new AssertionException("The native TextBox must supply its real IME client.");
        Assert.That(client.SupportsPreedit, Is.True); Assert.That(client.TextViewVisual, Is.SameAs(presenter));
        client.SetPreeditText("中", 1);
        Assert.That(host.View.IsComposing, Is.True); Assert.That(host.Controller.Misspellings, Is.Empty);
        Assert.That(host.Editor.Text, Is.EqualTo("wrld"), "Preedit is separate from the committed native text.");
        client.SetPreeditText("中文", 2); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(presenter.PreeditText, Is.EqualTo("中文")); Assert.That(host.View.IsComposing, Is.True);
        Assert.That(host.Editor.Text, Is.EqualTo("wrld"));
        Assert.That(host.Controller.Misspellings, Is.Empty); Assert.That(host.View.GetRangeRectangles(new(0, 4)), Is.Empty);
        client.SetPreeditText(null);
        host.Window.KeyTextInput("hello ");
        Assert.That(host.Editor.Text, Is.EqualTo("hello wrld")); Assert.That(host.View.IsComposing, Is.False);
        await host.Checked(1); Assert.That(host.Controller.Misspellings.Single().Start, Is.EqualTo(6));
    }

    [AvaloniaTest]
    public async Task FiveBoldSuggestionsIgnoreOneOccurrenceAndUserAddRemainRealAfterOtherChecks()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        host.Editor.Text = "wrld wrld"; await host.Checked(2);
        host.Editor.SelectionStart = host.Editor.SelectionEnd = 0; await host.OpenMenu();
        MenuItem[] suggestions = host.Menu.Items.OfType<MenuItem>().Where(item => item.Name == "SpellSuggestion").ToArray();
        Assert.That(suggestions.Length, Is.InRange(1, 5)); Assert.That(suggestions.Select(item => item.FontWeight), Is.All.EqualTo(FontWeight.Bold));
        Assert.That(suggestions.Select(item => item.Header), Does.Contain("world"));
        Invoke(host.MenuItem("IgnoreWord")); host.Menu.Close(); Assert.That(host.Controller.Misspellings.Single().Start, Is.EqualTo(5));
        DesktopPreferences flags = host.Desktop.Load() with { MarkIllFormedLinesInCommitMsg = false };
        host.Desktop.Save(flags); CommitSpellingController.PublishSavedDesktop(host.Desktop.FilePath, flags);
        await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Controller.Misspellings.Single().Start, Is.EqualTo(5), "Another check of the same text must not undo Ignore occurrence.");
        host.Editor.SelectionStart = host.Editor.SelectionEnd = 6; await host.OpenMenu();
        Invoke(host.MenuItem("AddToDictionary")); host.Menu.Close(); await host.Checked(0);
        Assert.That(File.ReadAllLines(host.Service.UserDictionaryFile), Does.Contain("wrld"));
        Assert.That(host.Desktop.Load().MarkIllFormedLinesInCommitMsg, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeReplacementAndRemovePreserveTheSelectionAndUndoHistory(bool remove)
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, "hello wrld"); await host.Checked(1);
        host.Editor.SelectionStart = 6; host.Editor.SelectionEnd = 10; await host.OpenMenu();
        int start = host.Editor.SelectionStart, end = host.Editor.SelectionEnd;
        MenuItem action = remove ? host.MenuItem("RemoveWord") : host.Menu.Items.OfType<MenuItem>().Single(item => item.Name == "SpellSuggestion" && item.Header as string == "world");
        Invoke(action); host.Menu.Close();
        Assert.That(host.Editor.Text, Is.EqualTo(remove ? "hello" : "hello world"));
        Assert.That(host.Editor.SelectionStart, Is.EqualTo(Math.Min(start, host.Editor.Text!.Length)));
        Assert.That(host.Editor.SelectionEnd, Is.EqualTo(Math.Min(end, host.Editor.Text!.Length)));
        Assert.That(host.Editor.CanUndo, Is.True); host.Editor.Undo();
        Assert.That(host.Editor.Text, Is.EqualTo("hello wrld"), "The unchanged native TextBox must own a real undo operation.");
    }

    [AvaloniaTest]
    public async Task APartiallyOverflowingSelectionIsCollapsedLikeWindowsAndTheNativeUndoStillRestoresTheText()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        const string original = "hello wrld";
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, original); await host.Checked(1);
        host.Editor.SelectionStart = 2; host.Editor.SelectionEnd = 10;
        await host.OpenMenu(position: 7);
        Assert.That(host.Editor.SelectionStart, Is.EqualTo(2)); Assert.That(host.Editor.SelectionEnd, Is.EqualTo(10));
        Invoke(host.MenuItem("RemoveWord")); host.Menu.Close();
        Assert.That(host.Editor.Text, Is.EqualTo("hello"));
        Assert.That(host.Editor.SelectionStart, Is.EqualTo(2)); Assert.That(host.Editor.SelectionEnd, Is.EqualTo(2),
            "Windows resets the whole selection length when it would extend beyond the shortened text.");
        Assert.That(host.Editor.CanUndo, Is.True); host.Editor.Undo();
        Assert.That(host.Editor.Text, Is.EqualTo(original));
    }

    [AvaloniaTest]
    public async Task ARealOldMenuReadCannotReplaceTheServicePublishedFromANewerSavedProfile()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        TaskCompletionSource oldReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdNextRead = 0; GitDictionarySettingsSnapshot? oldSnapshot = null;
        async Task<GitDictionarySettingsSnapshot> Read(string global, CancellationToken token)
        {
            GitDictionarySettingsSnapshot real = await git.Repository.ReadEffectiveDictionaryAsync(global, token);
            if (Interlocked.Exchange(ref holdNextRead, 0) == 1)
            {
                oldSnapshot = real; oldReadCompleted.TrySetResult();
                // The real IO finished. Keep this one owned response pending across a
                // newer publication, including if its consumer becomes obsolete.
                await releaseOldResponse.Task;
            }
            return real;
        }
        await using Host host = new(git.Repository, git.Output("profile"), "en-US", readSettingsAsync: Read);
        host.Editor.Text = "ordinateur"; await host.Checked(1);
        Interlocked.Exchange(ref holdNextRead, 1);
        try
        {
            await RequestContextMenu(host.Window, host.Editor, host.View);
            await oldReadCompleted.Task.WaitAsync(Limit);
            Assert.That(oldSnapshot!.EffectiveDictionary, Is.EqualTo("en-US"));
            DesktopPreferences saved = host.Desktop.Load() with { Dictionary = "fr-FR" };
            host.Desktop.Save(saved); CommitSpellingController.PublishSavedDesktop(host.Desktop.FilePath, saved);
            await Until(() => ServiceOrNull(host.Controller)?.DictionaryName == "fr-FR"); await host.Checked(0);
            SpellCheckService current = host.Service;
            Assert.That(host.Menu.IsOpen, Is.False, "A newer accepted profile invalidates the old menu snapshot.");
            releaseOldResponse.TrySetResult(); await host.Controller.Completion.WaitAsync(Limit);
            Assert.That(host.Service, Is.SameAs(current)); Assert.That(host.Service.DictionaryName, Is.EqualTo("fr-FR"));
            Assert.That(host.Controller.Misspellings, Is.Empty); Assert.That(host.Editor.Text, Is.EqualTo("ordinateur"));
            Assert.That(host.Desktop.Load().Dictionary, Is.EqualTo("fr-FR"));
        }
        finally { releaseOldResponse.TrySetResult(); await host.Controller.Completion.WaitAsync(Limit); }
    }

    [AvaloniaTest]
    [TestCase("wrld hello", 0, "hello")]
    [TestCase("hello wrld world", 6, "hello world")]
    [TestCase("hello wrld!", 6, "hello!")]
    [TestCase("hello wrld", 6, "hello")]
    [TestCase("wrld", 0, "")]
    public async Task RemoveUsesTheOriginalNetSpellWhitespaceRulesAndKeepsNativeUndo(string original, int wordStart, string expected)
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        await AvatarUiTestDriver.Edit(host.Window, host.Editor, original); await host.Checked(1);
        host.Editor.SelectionStart = host.Editor.SelectionEnd = wordStart;
        await host.OpenMenu(position: wordStart + 1);
        Invoke(host.MenuItem("RemoveWord")); host.Menu.Close();
        Assert.That(host.Editor.Text, Is.EqualTo(expected));
        Assert.That(host.Editor.CanUndo, Is.True); host.Editor.Undo();
        Assert.That(host.Editor.Text, Is.EqualTo(original));
    }

    [AvaloniaTest]
    public async Task RightClickUsesTheActuallyPointedWordInsteadOfTheUnrelatedCaret()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        host.Editor.Text = "wrld codexunknownwordqqq"; await host.Checked(2);
        host.Editor.SelectionStart = host.Editor.SelectionEnd = 0; using (await AvatarUiTestDriver.Frame(host.Window)) { }
        Rect target = host.View.GetRangeRectangles(new(5, 19)).Single();
        Control marks = host.View.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "SpellingUnderlines");
        Point point = marks.TranslatePoint(target.Center, host.Window)!.Value;
        Assert.That((host.Window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(host.Editor), Is.True);
        host.Window.MouseMove(point); host.Window.MouseDown(point, MouseButton.Right); host.Window.MouseUp(point, MouseButton.Right);
        await Until(() => host.Menu.IsOpen && host.Menu.Items.OfType<MenuItem>().Any(item => item.Name == "RemoveWord"));
        Invoke(host.MenuItem("RemoveWord")); host.Menu.Close();
        Assert.That(host.Editor.Text, Is.EqualTo("wrld"), "The word under the pointer and its adjacent last-word space are removed; the caret's first word remains.");
    }

    [AvaloniaTest]
    public async Task TheMenuWritesOnlyItsGlobalFlagWithoutOverwritingPeerPreferences()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        DesktopPreferences peer = host.Desktop.Load() with { Theme = "Dark", HistoryLimit = 713, ShowRepoCurrentBranch = false };
        host.Desktop.Save(peer);
        host.Editor.Text = "wrld"; await host.Checked(1); await host.OpenMenu();
        Assert.That(ToolTip.GetTip(host.MenuItem("ProvideAutocompletion")), Is.Null, "Git completion now has a real controller; remove its obsolete unavailable-feature diagnostic.");
        Invoke(host.MenuItem("ProvideAutocompletion")); host.Menu.Close();
        DesktopPreferences saved = host.Desktop.Load();
        Assert.That(saved.ProvideAutocompletion, Is.False); Assert.That(saved.Theme, Is.EqualTo("Dark"));
        Assert.That(saved.HistoryLimit, Is.EqualTo(713)); Assert.That(saved.ShowRepoCurrentBranch, Is.False);
        host.Editor.SelectionStart = host.Editor.SelectionEnd = 0; await host.OpenMenu();
        Assert.That(host.Menu.Items.OfType<MenuItem>().Any(item => item.Name is "SpellSuggestion" or "AddToDictionary"), Is.False);
        Assert.That(host.Menu.Items.OfType<MenuItem>().Select(item => item.Name), Does.Contain("SpellingCut").And.Contain("SpellingCopy").And.Contain("SpellingPaste").And.Contain("SpellingDelete").And.Contain("SpellingSelectAll"));
        host.Menu.Close();
    }

    [AvaloniaTest]
    public async Task TheRealIllFormedOverlayPaints50Zero72AndDoesNotRewriteTheMessage()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "none");
        const string separator = "\r\n";
        string text = new string('a', 50) + "     " + separator + "x" + separator + new string('b', 72) + "   ";
        DesktopPreferences before = host.Desktop.Load() with { MarkIllFormedLinesInCommitMsg = false };
        host.Desktop.Save(before); CommitSpellingController.PublishSavedDesktop(host.Desktop.FilePath, before);
        host.Editor.Text = text; await host.Ready();
        SpellingRange[] ranges = SpellingEditorView.IllFormedRanges(text).ToArray();
        Assert.That(ranges, Is.EqualTo(new[] { new SpellingRange(50, 5), new SpellingRange(57, 1), new SpellingRange(132, 3) }));
        using WriteableBitmap plain = await AvatarUiTestDriver.Frame(host.Window);
        DesktopPreferences marked = before with { MarkIllFormedLinesInCommitMsg = true };
        host.Desktop.Save(marked); CommitSpellingController.PublishSavedDesktop(host.Desktop.FilePath, marked);
        using WriteableBitmap frame = await AvatarUiTestDriver.Frame(host.Window);
        Control marks = host.View.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "SpellingUnderlines");
        foreach (SpellingRange range in ranges)
        {
            Rect rectangle = host.View.GetRangeRectangles(range).First();
            Point point = marks.TranslatePoint(new Point(rectangle.Left + 2, rectangle.Top + 2), host.Window)!.Value;
            int x = (int)Math.Round(point.X * host.Window.RenderScaling), y = (int)Math.Round(point.Y * host.Window.RenderScaling);
            Color plainPixel = AvatarUiTestDriver.Pixel(plain, x, y), markedPixel = AvatarUiTestDriver.Pixel(frame, x, y);
            Assert.That(markedPixel, Is.Not.EqualTo(plainPixel), "Every overflowing line must really be painted: " + range);
            Assert.That(markedPixel.R, Is.GreaterThan(markedPixel.B));
        }
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "commit-spelling-marks-50-0-72.png");
        frame.Save(output); TestContext.AddTestAttachment(output, "Real TextPresenter ranges painted by the passive commit overlay");
        Assert.That(host.Editor.Text, Is.EqualTo(text)); Assert.That(host.Controller.Misspellings, Is.Empty);
    }

    [AvaloniaTest]
    public async Task APeerLocalEditAfterOpeningTheMenuIsAVisibleCasConflict()
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), "en-US");
        await host.OpenMenu(); MenuItem staleChoice = host.MenuItem("CommitDictionary_none");
        GitDictionarySettingsSnapshot peer = await git.Repository.ReadEffectiveDictionaryAsync("en-US");
        await git.Repository.WriteLocalDictionaryAsync(peer, "fr-FR");
        Invoke(staleChoice); host.Menu.Close(); await Until(() => host.Controller.Notice.IsVisible);
        Assert.That(host.Controller.Notice.Text, Is.Not.Null.And.Not.Empty);
        Assert.That((await git.Repository.ReadEffectiveDictionaryAsync("en-US")).LocalDictionary, Is.EqualTo("fr-FR"));
    }

    [AvaloniaTest]
    [TestCase("not-installed")]
    [TestCase(" en-US ")]
    [TestCase("en-us")]
    public async Task MissingAndInvalidDictionaryErrorsAreVisibleEvenForAnEmptyMessage(string identity)
    {
        using GitFixture git = await GitFixture.CreateAsync(); await using Host host = new(git.Repository, git.Output("profile"), identity);
        await Until(() => host.Controller.Notice.IsVisible);
        Assert.That(host.Controller.Notice.Text, Does.Contain(identity)); Assert.That(host.Editor.Text, Is.Null.Or.Empty);
        Assert.That(host.Desktop.Load().Dictionary, Is.EqualTo(identity));
    }

    [AvaloniaTest]
    public async Task AnUnreadableDesktopProfileIsNeverReplacedByDefaults()
    {
        using GitFixture git = await GitFixture.CreateAsync(); string directory = git.Output("bad-profile");
        PreferencesStore desktop = new(directory); Directory.CreateDirectory(Path.GetDirectoryName(desktop.FilePath)!);
        const string invalid = "{invalid-commit-spelling-profile"; File.WriteAllText(desktop.FilePath, invalid);
        await using Host host = new(git.Repository, directory, null, preserveProfile: true);
        await Until(() => host.Controller.Notice.IsVisible); Assert.That(File.ReadAllText(desktop.FilePath), Is.EqualTo(invalid));
        Assert.That(ServiceOrNull(host.Controller), Is.Null);
    }

    [AvaloniaTest]
    public async Task ActualCommitClosedOccursAfterCurrentRetiredAndPendingEditorWorkHaveDrainedAndTheDraftIsSaved()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        PreferencesStore desktop = new(); desktop.Save(new() { Dictionary = "en-US", Language = "English" });
        CommitDialog dialog = new(git.Repository); dialog.Show(); await dialog.InitializeAsync();
        Assert.That(dialog.LastError, Is.Null);
        CommitSpellingController controller = Private<CommitSpellingController>(dialog, "_spelling");
        bool closed = false, drainedInClosed = false;
        dialog.Closed += (_, _) =>
        {
            closed = true; drainedInClosed = dialog.SpellingCloseCompletion.IsCompletedSuccessfully
                && Private<List<SpellCheckService>>(controller, "_services").All(service => service.CloseCompletion.IsCompletedSuccessfully);
        };
        try
        {
            await Until(() => ServiceOrNull(controller) is not null);
            TextBox editor = Find<TextBox>(dialog, "Message"); ContextMenu menu = editor.ContextMenu!;
            await RequestContextMenu(dialog, editor, Private<SpellingEditorView>(controller, "_view"));
            await Until(() => menu.Items.OfType<MenuItem>().Single(item => item.Name == "CommitDictionary").IsEnabled);
            Invoke(menu.Items.OfType<MenuItem>().Single(item => item.Name == "CommitDictionary").Items.OfType<MenuItem>().Single(item => item.Name == "CommitDictionary_fr-FR")); menu.Close();
            await Until(() => ServiceOrNull(controller)?.DictionaryName == "fr-FR");
            Assert.That(Private<List<SpellCheckService>>(controller, "_services").Count, Is.GreaterThanOrEqualTo(2));
            Find<TextBox>(dialog, "Message").Text = "Preserve this draft wrld";
            dialog.Close(); await Until(() => closed); await dialog.SpellingCloseCompletion.WaitAsync(Limit);
            Assert.That(drainedInClosed, Is.True, "The real Closed event must see actual service and consumer completion, including retired languages.");
        }
        finally
        {
            if (!closed) { dialog.Close(); await Until(() => closed); }
            await dialog.SpellingCloseCompletion.WaitAsync(Limit);
        }
        CommitDialog reopened = new(git.Repository); reopened.Show(); await reopened.InitializeAsync();
        try { Assert.That(reopened.LastError, Is.Null); Assert.That(Find<TextBox>(reopened, "Message").Text, Is.EqualTo("Preserve this draft wrld")); }
        finally { await CloseCommit(reopened); }
    }

    [AvaloniaTest]
    public async Task AutoCloseKeepsTheFinishedCommitLockedUntilItsOwnedRealSettingsReplyDrains()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        new PreferencesStore().Save(new() { Dictionary = "none", Language = "English" });
        await File.WriteAllTextAsync(git.File, "base\nstaged change\n");
        await git.Repository.StageAllAsync();
        int before = int.Parse((await git.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        TaskCompletionSource<GitDictionarySettingsSnapshot> readCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GitDictionarySettingsSnapshot> Read(string dictionary, CancellationToken token)
        {
            GitDictionarySettingsSnapshot actual = await git.Repository.ReadEffectiveDictionaryAsync(dictionary, token);
            readCompleted.TrySetResult(actual);
            await release.Task; // Hold the genuine reply after IO; cancellation cannot pretend this task is drained.
            return actual;
        }
        CommitDialog dialog = new(git.Repository, null, null, TargetedCommitKind.Normal, null, Read);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool drainedAtClosed = false;
        dialog.Closed += (_, _) =>
        {
            drainedAtClosed = dialog.SpellingCloseCompletion.IsCompletedSuccessfully;
            closed.TrySetResult();
        };
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await readCompleted.Task.WaitAsync(Limit);
            Assert.That(dialog.LastError, Is.Null);
            TextBox message = Find<TextBox>(dialog, "Message");
            message.Text = "Commit while a real dictionary reply is held";
            Assert.That(Find<Button>(dialog, "Commit").IsEnabled, Is.True);
            // Await the real workflow task, including its finally, rather than infer completion from a flag.
            Task committing = (Task)typeof(CommitDialog).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [false])!;
            await committing.WaitAsync(Limit);
            Assert.That(dialog.HasCommitted, Is.True); Assert.That(dialog.LastError, Is.Null);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(closed.Task.IsCompleted, Is.False);
            Assert.That(dialog.SpellingCloseCompletion.IsCompleted, Is.False, "The real owned reply is still held.");
            Assert.That(dialog.IsBusy, Is.True, "CommitAsync finally must not erase the independent closing state.");
            Assert.That(message.IsEnabled, Is.False);
            foreach (string name in new[] { "Commit", "CommitAndPush", "Stage", "Unstage", "StageAll", "UnstageAll" })
            { Assert.That(Find<Button>(dialog, name).IsEnabled, Is.False, name + " must remain unavailable during the drain."); }
            GitCommandLog.Clear();
            await dialog.RefreshAsync();
            Task repeatedCommit = (Task)typeof(CommitDialog).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [false])!;
            await repeatedCommit.WaitAsync(Limit);
            Assert.That(GitCommandLog.Snapshot(), Is.Empty, "Neither refresh nor a repeated commit may admit Git work after closure started.");
            Assert.That(int.Parse((await git.GitAsync("rev-list", "--count", "HEAD")).StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(before + 1));
            release.TrySetResult();
            await closed.Task.WaitAsync(Limit); await dialog.SpellingCloseCompletion.WaitAsync(Limit);
            Assert.That(drainedAtClosed, Is.True); Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.IsBusy, Is.False);
        }
        finally
        {
            release.TrySetResult();
            if (!closed.Task.IsCompleted) { dialog.Close(); await closed.Task.WaitAsync(Limit); }
            await dialog.SpellingCloseCompletion.WaitAsync(Limit);
        }
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static T Private<T>(object instance, string name) where T : class
        => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)
            ?? throw new AssertionException(name + " is not initialized."));
    private static SpellCheckService? ServiceOrNull(CommitSpellingController controller)
        => (SpellCheckService?)typeof(CommitSpellingController).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller);
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Invoke(MenuItem item) { Assert.That(item.IsEnabled, Is.True, item.Name); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < Limit) { await Task.Delay(10); }
        Assert.That(complete(), Is.True, "The real editor operation did not complete.");
    }
    private static async Task RequestContextMenu(Window window, TextBox editor, SpellingEditorView view, int? position = null)
    {
        using (await AvatarUiTestDriver.Frame(window)) { }
        int length = editor.Text?.Length ?? 0;
        int index = Math.Clamp(position ?? Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, length);
        Point point;
        if (length == 0) { point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value; }
        else
        {
            Rect target = view.GetRangeRectangles(new(Math.Min(index, length - 1), 1)).First();
            Control marks = view.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "SpellingUnderlines");
            point = marks.TranslatePoint(target.Center, window)!.Value;
        }
        Assert.That((window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(editor), Is.True,
            "The real context gesture must hit the published native editor, with no Focus repair.");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
        await Until(() => editor.ContextMenu?.IsOpen == true);
    }

    private static async Task CloseCommit(CommitDialog dialog)
    {
        bool closed = false; dialog.Closed += (_, _) => closed = true; dialog.Close();
        await Until(() => closed); await dialog.SpellingCloseCompletion.WaitAsync(Limit);
    }

    private sealed class Host : IAsyncDisposable
    {
        internal Window Window { get; }
        internal TextBox Editor { get; } = new() { Name = "Message", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260, FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12 };
        internal SpellingEditorView View { get; }
        internal CommitSpellingController Controller { get; }
        internal PreferencesStore Desktop { get; }
        internal SpellCheckService Service => ServiceOrNull(Controller) ?? throw new AssertionException("The real engine has not been selected.");
        internal ContextMenu Menu => Editor.ContextMenu!;
        internal Host(GitRepository repository, string directory, string? dictionary, DictionaryCatalog? catalog = null, bool preserveProfile = false,
            Func<string, CancellationToken, Task<GitDictionarySettingsSnapshot>>? readSettingsAsync = null)
        {
            Desktop = new(directory);
            if (!preserveProfile) { Desktop.Save(new() { Dictionary = dictionary ?? "en-US", Language = "English", MarkIllFormedLinesInCommitMsg = true }); }
            View = new(Editor); Controller = new(repository, View, Desktop, catalog, readSettingsAsync);
            Window = new() { Width = 800, Height = 320, Content = new StackPanel { Children = { View, Controller.Notice } } };
            Window.Show(); Controller.Start();
        }
        internal async Task Ready()
        { await Until(() => ServiceOrNull(Controller) is not null); await Service.Ready.WaitAsync(Limit); }
        internal async Task Checked(int count)
        {
            await Ready();
            await Until(() =>
            {
                SpellCheckResult? result = (SpellCheckResult?)typeof(CommitSpellingController).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Controller);
                return (!Service.IsEnabled || result is not null && result.Text == (Editor.Text ?? "") && result.DictionaryName == Service.DictionaryName)
                    && Controller.Misspellings.Count == count;
            });
            Assert.That(Controller.Misspellings.Count, Is.EqualTo(count));
        }
        internal async Task OpenMenu(int? position = null)
        {
            await Ready(); await RequestContextMenu(Window, Editor, View, position);
            await Until(() => Menu.IsOpen && Menu.Items.OfType<MenuItem>().Any(item => item.Name == "CommitDictionary" && item.IsEnabled));
        }
        internal MenuItem MenuItem(string name)
            => Menu.Items.OfType<MenuItem>().Concat(Menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>())).Single(item => item.Name == name);
        public async ValueTask DisposeAsync()
        {
            Menu.Close(); await Controller.BeginClose().WaitAsync(Limit);
            Window.Close();
        }
    }
}
