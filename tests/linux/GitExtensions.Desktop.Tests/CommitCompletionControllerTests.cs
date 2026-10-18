using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitUI.AutoCompletion;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitCompletionControllerTests
{
    [AvaloniaTest]
    public async Task TypingUsesTheTwoCharacterDebounceAndNeverReloadsTheCorpusPerKey()
    {
        await using Host host = new(); int reads = 0;
        host.Controller.Refresh(_ => { Interlocked.Increment(ref reads); return Words("CloneRepository", "CloseWindow"); });
        await host.Controller.Completion.WaitAsync(Limit);
        await host.Edit(""); Assert.That(host.Popup.IsOpen, Is.False);
        host.Window.KeyTextInput("C"); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Popup.IsOpen, Is.False, "One typed character does not show automatic completion.");
        host.Window.KeyTextInput("l");
        Assert.That(host.Popup.IsOpen, Is.False, "The actual 200 ms timer must not publish synchronously.");
        Assert.That(host.Controller.Completion.IsCompleted, Is.False);
        await host.Controller.Completion.WaitAsync(Limit); await host.Frame();
        Assert.That(host.Suggestions, Is.EqualTo(new[] { "CloneRepository", "CloseWindow" }));
        Assert.That(host.Editor.Text, Is.EqualTo("Cl")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        host.Window.KeyTextInput("o"); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Editor.Text, Is.EqualTo("Clo")); Assert.That(reads, Is.EqualTo(1));
        host.Window.KeyTextInput(" "); Assert.That(host.Popup.IsOpen, Is.False);
        Assert.That(host.Editor.Text, Is.EqualTo("Clo "));
    }

    [AvaloniaTest]
    [TestCase("")]
    [TestCase("C")]
    [TestCase("Co")]
    public async Task ControlSpaceAcceptsASingleResultAtZeroOneAndTwoCharacters(string prefix)
    {
        await using Host host = new(); await host.Load("Co-authored-by: "); await host.Edit(prefix);
        Press(host, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Editor.Text, Is.EqualTo("Co-authored-by: "));
        Assert.That(host.Popup.IsOpen, Is.False); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Editor.SelectedText, Is.Empty); Assert.That(host.DefaultClicks, Is.Zero);
        host.Editor.Undo(); Assert.That(host.Editor.Text ?? "", Is.EqualTo(prefix), "Acceptance must be one native undoable replacement.");
    }

    [AvaloniaTest]
    [TestCase("Change .g", ".gitignore", "Change .gitignore")]
    [TestCase("Change foo_b", "foo_bar", "Change foo_bar")]
    [TestCase("Change foo+", "foo+bar", "Change foo+bar")]
    [TestCase("Change x-y", "x-yet", "Change x-yet")]
    [TestCase("Namespace.GCS", "GitCommitSnapshot", "Namespace.GitCommitSnapshot")]
    [TestCase(").gi", "gitignore", ").gitignore")]
    public async Task SharedWindowsBoundariesAndCamelHumpsReplaceOnlyThePrefix(string message, string word, string expected)
    {
        await using Host host = new(); await host.Load(word); await host.Edit(message);
        Press(host, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Editor.Text, Is.EqualTo(expected)); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        host.Editor.Undo(); Assert.That(host.Editor.Text, Is.EqualTo(message));
    }

    [AvaloniaTest]
    public async Task ExplicitActivationKeepsShortPrefixesEligibleUntilTheUserDismissesIt()
    {
        await using Host host = new(); await host.Load("CloneRepository", "CloseWindow"); await host.Edit("");
        Press(host, PhysicalKey.Space, RawInputModifiers.Control); Assert.That(host.Popup.IsOpen, Is.True);
        host.Window.KeyTextInput("C"); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Popup.IsOpen, Is.True, "Ctrl+Space activation also applies to the following one-character prefix.");
        Assert.That(host.Editor.Text, Is.EqualTo("C"));
        Press(host, PhysicalKey.Escape); Assert.That(host.Popup.IsOpen, Is.False);
        Press(host, PhysicalKey.Backspace); host.Window.KeyTextInput("C"); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Popup.IsOpen, Is.False, "Escape returns to the ordinary two-character admission rule.");
    }

    [AvaloniaTest]
    public async Task MoreThanFiveSortedResultsWrapAndPageWithoutMovingTheNativeCaret()
    {
        await using Host host = new(); string[] expected = Enumerable.Range(0, 24).Select(index => "Commit" + index.ToString("D2")).ToArray();
        await host.Load(expected.Reverse().Concat([expected[0]]).ToArray()); await host.Edit("Co");
        await host.Controller.Completion.WaitAsync(Limit); await host.Frame();
        Assert.That(host.Suggestions, Is.EqualTo(expected)); Assert.That(host.Suggestions.Count, Is.EqualTo(24));
        var original = host.State;
        Press(host, PhysicalKey.ArrowUp); Assert.That(host.Selected, Is.EqualTo(expected[^1]));
        Press(host, PhysicalKey.ArrowDown); Assert.That(host.Selected, Is.EqualTo(expected[0]));
        Press(host, PhysicalKey.PageDown); Assert.That(host.List.SelectedIndex, Is.InRange(1, expected.Length - 2));
        Press(host, PhysicalKey.PageUp); Assert.That(host.Selected, Is.EqualTo(expected[0]));
        Press(host, PhysicalKey.End); Assert.That(host.Selected, Is.EqualTo(expected[^1]));
        Press(host, PhysicalKey.Home); Assert.That(host.Selected, Is.EqualTo(expected[0]));
        Assert.That(host.State, Is.EqualTo(original)); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Press(host, PhysicalKey.Escape); Assert.That(host.CancelClicks, Is.Zero); Assert.That(host.Window.IsVisible, Is.True);
        Assert.That(host.State, Is.EqualTo(original));
    }

    [AvaloniaTest]
    [TestCase(PhysicalKey.Enter)]
    [TestCase(PhysicalKey.Tab)]
    public async Task AcceptKeysConsumeTheWindowShortcutAndKeepNativeUndo(PhysicalKey key)
    {
        await using Host host = new(); await host.Load("CloneRepository", "CloseWindow"); await host.Edit("Cl");
        await host.Controller.Completion.WaitAsync(Limit); Assert.That(host.Popup.IsOpen, Is.True);
        Press(host, PhysicalKey.ArrowDown); Press(host, key);
        Assert.That(host.Editor.Text, Is.EqualTo("CloseWindow")); Assert.That(host.Popup.IsOpen, Is.False);
        Assert.That(host.DefaultClicks, Is.Zero); Assert.That(host.CancelClicks, Is.Zero);
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True); Assert.That(host.Editor.SelectedText, Is.Empty);
        Press(host, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(host.Editor.Text, Is.EqualTo("Cl"));
    }

    [AvaloniaTest]
    public async Task ShowingAndDismissingAPopupPreservesTheNativeSelectionAndItsReplacementLeavesTheTail()
    {
        await using Host host = new(); await host.Load("Co-authored-by: ", "Commit"); await host.Edit("Co suffix");
        Press(host, PhysicalKey.Home); Press(host, PhysicalKey.ArrowRight); Press(host, PhysicalKey.ArrowRight);
        Press(host, PhysicalKey.ArrowRight, RawInputModifiers.Shift); var selection = host.State;
        Assert.That(host.Editor.SelectedText, Is.EqualTo(" "));
        Press(host, PhysicalKey.Space, RawInputModifiers.Control); await host.Frame();
        Assert.That(host.Popup.IsOpen, Is.True); Assert.That(host.State, Is.EqualTo(selection));
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Press(host, PhysicalKey.Escape); Assert.That(host.State, Is.EqualTo(selection));
        Press(host, PhysicalKey.Space, RawInputModifiers.Control); Press(host, PhysicalKey.Enter);
        Assert.That(host.Editor.Text, Is.EqualTo("Co-authored-by:  suffix"));
        host.Editor.Undo(); Assert.That(host.Editor.Text, Is.EqualTo("Co suffix"));
    }

    [AvaloniaTest]
    public async Task ClickingARealPopupRowNeverTransfersFocusAndAcceptsOnlyOnTheMatchingRelease()
    {
        await using Host host = new(); await host.Load("CloneRepository", "CloseWindow"); await host.Edit("Cl");
        await host.Controller.Completion.WaitAsync(Limit); await host.Frame();
        ListBoxItem row = host.List.GetVisualDescendants().OfType<ListBoxItem>().Single(item => (item.Content as AutoCompleteWord)?.Word == "CloseWindow");
        TopLevel popupRoot = (TopLevel)row.GetVisualRoot()!; using (await AvatarUiTestDriver.Frame(popupRoot)) { }
        Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), popupRoot)!.Value;
        Assert.That((popupRoot.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
        var before = host.State;
        popupRoot.MouseMove(point); popupRoot.MouseDown(point, MouseButton.Left);
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True); Assert.That(host.State, Is.EqualTo(before));
        popupRoot.MouseUp(point, MouseButton.Left);
        Assert.That(host.Editor.Text, Is.EqualTo("CloseWindow")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Popup.IsOpen, Is.False); host.Editor.Undo(); Assert.That(host.Editor.Text, Is.EqualTo("Cl"));
    }

    [AvaloniaTest]
    [TestCase("caret")]
    [TestCase("selection")]
    [TestCase("pointer")]
    [TestCase("focus")]
    public async Task NativeInteractionChangesInvalidateThePendingTimerEvenWhenTheCaretOrFocusReturns(string gesture)
    {
        await using Host host = new(); await host.Load("CloneRepository", "CloseWindow"); await host.Edit("Cl");
        Task pending = host.Controller.Completion;
        Assert.That(pending.IsCompleted, Is.False);
        if (gesture == "caret") { Press(host, PhysicalKey.ArrowLeft); Press(host, PhysicalKey.ArrowRight); }
        else if (gesture == "selection") { Press(host, PhysicalKey.ArrowLeft, RawInputModifiers.Shift); Press(host, PhysicalKey.ArrowRight); }
        else if (gesture == "pointer") { await AvatarUiTestDriver.Click(host.Window, host.Editor); }
        else { await AvatarUiTestDriver.Click(host.Window, host.Other); await AvatarUiTestDriver.Click(host.Window, host.Editor); }
        var state = host.State; await pending.WaitAsync(Limit); await host.Frame();
        Assert.That(host.Popup.IsOpen, Is.False); Assert.That(host.State, Is.EqualTo(state));
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task RealImePreeditClosesAndSuspendsCompletionUntilNativeCommittedInput()
    {
        await using Host host = new(); await host.Load("CloneRepository", "CloseWindow"); await host.Edit("Cl");
        await host.Controller.Completion.WaitAsync(Limit); await host.Frame(); Assert.That(host.Popup.IsOpen, Is.True);
        TextPresenter presenter = host.Editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        TextInputMethodClientRequestedEventArgs request = new() { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        host.Editor.RaiseEvent(request); TextInputMethodClient client = request.Client ?? throw new AssertionException("Native TextBox IME client missing.");
        Assert.That(client.SupportsPreedit, Is.True); Assert.That(client.TextViewVisual, Is.SameAs(presenter));
        client.SetPreeditText("中", 1); Assert.That(host.View.IsComposing, Is.True); Assert.That(host.Popup.IsOpen, Is.False);
        var before = host.State; Press(host, PhysicalKey.Space, RawInputModifiers.Control);
        await host.Controller.Completion.WaitAsync(Limit); Assert.That(host.Popup.IsOpen, Is.False); Assert.That(host.State, Is.EqualTo(before));
        client.SetPreeditText(null); host.Window.KeyTextInput("o"); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.View.IsComposing, Is.False); Assert.That(host.Popup.IsOpen, Is.True);
        Assert.That(host.Editor.Text, Is.EqualTo("Clo")); Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    public async Task DisabledCompletionStartsNoProviderAndRetiredRawRepliesCannotPublishOrOpenThePopup()
    {
        await using Host host = new(); host.Controller.SetEnabled(false); int starts = 0;
        TaskCompletionSource entered = Signal(), release = Signal(); CancellationToken firstToken = default;
        List<string[]> publications = []; host.Controller.CorpusPublished += words => publications.Add(words.ToArray());
        host.Controller.Refresh(async token => { Interlocked.Increment(ref starts); firstToken = token; entered.TrySetResult(); await release.Task; return [new AutoCompleteWord("Retired")]; });
        Assert.That(starts, Is.Zero); await host.Edit("Re"); Press(host, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Popup.IsOpen, Is.False); host.Controller.SetEnabled(true); await entered.Task.WaitAsync(Limit);
        host.Controller.SetEnabled(false); Assert.That(firstToken.IsCancellationRequested, Is.True);
        Assert.That(host.Controller.Completion.IsCompleted, Is.False, "Cancellation cannot stand in for the actual provider ending.");
        try
        {
            host.Controller.Refresh(_ => Words("Current")); host.Controller.SetEnabled(true);
            await Until(() => publications.Any(words => words.SequenceEqual(["Current"])));
            release.TrySetResult(); await host.Controller.Completion.WaitAsync(Limit);
            Assert.That(publications.Any(words => words.Contains("Retired")), Is.False); Assert.That(host.Popup.IsOpen, Is.False);
            await host.Edit("Cu"); Press(host, PhysicalKey.Space, RawInputModifiers.Control);
            Assert.That(host.Editor.Text, Is.EqualTo("Current")); Assert.That(starts, Is.EqualTo(1));
        }
        finally { release.TrySetResult(); }
    }

    [AvaloniaTest]
    public async Task ClosingDrainsAnUncooperativeRawProviderAndDoesNotPublishItsLateSuccess()
    {
        await using Host host = new(); TaskCompletionSource entered = Signal(), release = Signal(); List<string[]> publications = [];
        host.Controller.CorpusPublished += words => publications.Add(words.ToArray());
        host.Controller.Refresh(async _ => { entered.TrySetResult(); await release.Task; return [new AutoCompleteWord("Late")]; });
        await entered.Task.WaitAsync(Limit); await host.Edit("La");
        Task closing = host.Controller.BeginClose(); Assert.That(closing.IsCompleted, Is.False);
        var state = host.State;
        release.TrySetResult(); await closing.WaitAsync(Limit);
        Assert.That(host.Controller.Completion, Is.SameAs(closing)); Assert.That(host.Controller.Completion.IsCompletedSuccessfully, Is.True);
        Assert.That(publications.Any(words => words.Contains("Late")), Is.False); Assert.That(host.Popup.IsOpen, Is.False);
        Assert.That(host.State, Is.EqualTo(state)); Assert.That(host.Controller.Notice.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task CorpusPublicationCopiesDeferredMutableInputAndDoesNotReplayAnOldKeyboardRequest()
    {
        await using Host host = new(); TaskCompletionSource entered = Signal(), release = Signal();
        List<AutoCompleteWord> supplied = [new("CloneRepository"), new("CloseWindow")];
        host.Controller.Refresh(async _ => { entered.TrySetResult(); await release.Task; return supplied; });
        try
        {
            await entered.Task.WaitAsync(Limit); await host.Edit("Cl");
            Press(host, PhysicalKey.Space, RawInputModifiers.Control);
            Assert.That(host.Controller.Notice.IsVisible, Is.True); Assert.That(host.Popup.IsOpen, Is.False);
            Press(host, PhysicalKey.ArrowLeft, RawInputModifiers.Shift); var state = host.State;
            release.TrySetResult(); await host.Controller.Completion.WaitAsync(Limit);
            supplied.Clear(); supplied.Add(new("ChangedAfterPublication"));
            Assert.That(host.Controller.Corpus, Is.EqualTo(new[] { "CloneRepository", "CloseWindow" }));
            Assert.That(host.Popup.IsOpen, Is.False); Assert.That(host.State, Is.EqualTo(state));
            Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True); Assert.That(host.Controller.Notice.IsVisible, Is.False);
        }
        finally { release.TrySetResult(); }
    }

    [AvaloniaTest]
    public async Task LatestProviderErrorsAreVisibleWhileRetiredFailuresStayDiagnosticOnly()
    {
        await using Host host = new(); TaskCompletionSource entered = Signal(), release = Signal();
        host.Controller.Refresh(async _ => { entered.TrySetResult(); await release.Task; throw new IOException("retired read failed"); });
        await entered.Task.WaitAsync(Limit);
        host.Controller.Refresh(_ => throw new IOException("current read failed"));
        await Until(() => host.Controller.Notice.IsVisible);
        Assert.That(host.Controller.Notice.Text, Does.Contain("current read failed"));
        release.TrySetResult(); await host.Controller.Completion.WaitAsync(Limit);
        Assert.That(host.Controller.Notice.Text, Does.Contain("current read failed").And.Not.Contain("retired"));
        Assert.That(host.Controller.Failures.Select(error => error.Message), Is.EquivalentTo(new[] { "retired read failed", "current read failed" }));
        await host.Load("Current"); Assert.That(host.Controller.Notice.IsVisible, Is.False);
        await host.Edit("Cu"); Press(host, PhysicalKey.Space, RawInputModifiers.Control);
        Assert.That(host.Editor.Text, Is.EqualTo("Current"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task PopupFollowsTheRealWrappedAndScrolledCaretAndDismissesWhenItLeavesTheViewport(bool dark)
    {
        await using Host host = new(dark ? ThemeVariant.Dark : ThemeVariant.Light);
        await host.Load("CloneRepository", "CloseWindow");
        string text = string.Join('\n', Enumerable.Repeat("A long wrapped subject with enough text to use the native editor layout repeatedly.", 30)) + "\nCl";
        await host.Edit(text); await host.Controller.Completion.WaitAsync(Limit); await host.Frame();
        Assert.That(host.Popup.IsOpen, Is.True); var original = host.State;
        TextPresenter presenter = host.Editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        Rect character = presenter.TextLayout.HitTestTextPosition(text.Length);
        Point nativeCaret = presenter.TranslatePoint(character.Position, host.Editor)!.Value;
        Assert.That(host.Popup.PlacementRect!.Value.X, Is.EqualTo(nativeCaret.X).Within(1));
        Assert.That(host.Popup.PlacementRect.Value.Y, Is.EqualTo(nativeCaret.Y).Within(1));
        PixelPoint onScreen = host.Editor.PointToScreen(nativeCaret);
        PixelPoint popupOnScreen = host.List.PointToScreen(default);
        Assert.That(popupOnScreen.X, Is.EqualTo(onScreen.X).Within(2));
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True); Assert.That(host.State, Is.EqualTo(original));
        await Capture(host.Window, "commit-completion-" + (dark ? "dark" : "light"));
        if (host.List.GetVisualRoot() is TopLevel popupRoot && popupRoot != host.Window)
        { await Capture(popupRoot, "commit-completion-popup-" + (dark ? "dark" : "light")); }
        ScrollViewer scroll = host.Editor.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.That(scroll.Offset.Y, Is.GreaterThan(0), "The real TextBox viewport must have scrolled to the trailing caret.");
        scroll.Offset = default; await host.Frame();
        Assert.That(host.Popup.IsOpen, Is.False); Assert.That(host.State, Is.EqualTo(original));
        Assert.That(host.Editor.IsKeyboardFocusWithin, Is.True);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<IEnumerable<AutoCompleteWord>> Words(params string[] words)
        => Task.FromResult<IEnumerable<AutoCompleteWord>>(words.Select(word => new AutoCompleteWord(word)).ToArray());
    private static void Press(Host host, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        => AvatarUiTestDriver.Press(host.Window, key, modifiers);
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch watch = Stopwatch.StartNew(); while (!complete() && watch.Elapsed < Limit) { await Task.Delay(10); }
        Assert.That(complete(), Is.True, "The owned native editor operation did not finish.");
    }
    private static async Task Capture(TopLevel top, string name)
    {
        using var frame = await AvatarUiTestDriver.Frame(top);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
    }

    private sealed class Host : IAsyncDisposable
    {
        internal TextBox Editor { get; } = new()
        { Name = "CommitMessage", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 12 };
        internal Button Other { get; } = new() { Name = "Other", Content = "Other" };
        internal SpellingEditorView View { get; }
        internal CommitCompletionController Controller { get; }
        internal Window Window { get; }
        internal int DefaultClicks { get; private set; }
        internal int CancelClicks { get; private set; }
        internal Popup Popup => View.Children.OfType<Popup>().Single();
        internal ListBox List => (ListBox)Popup.Child!;
        internal IReadOnlyList<string> Suggestions => List.Items.Cast<AutoCompleteWord>().Select(word => word.Word).ToArray();
        internal string? Selected => (List.SelectedItem as AutoCompleteWord)?.Word;
        internal (string? Text, int Caret, int Start, int End) State => (Editor.Text, Editor.CaretIndex, Editor.SelectionStart, Editor.SelectionEnd);
        internal Host(ThemeVariant? theme = null)
        {
            View = new(Editor); Controller = new(View);
            Grid root = new() { RowDefinitions = new("*,Auto,Auto"), Margin = new Thickness(10) };
            root.Children.Add(View); Grid.SetRow(Controller.Notice, 1); root.Children.Add(Controller.Notice);
            Button accept = new() { Content = "Commit", IsDefault = true }, cancel = new() { Content = "Cancel", IsCancel = true };
            accept.Click += (_, _) => DefaultClicks++; cancel.Click += (_, _) => CancelClicks++;
            StackPanel actions = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { Other, accept, cancel } };
            Grid.SetRow(actions, 2); root.Children.Add(actions);
            Window = new() { Width = 640, Height = 360, Content = root, RequestedThemeVariant = theme ?? ThemeVariant.Light };
            Window.AddHandler(InputElement.KeyDownEvent, (_, args) => Controller.HandleKeyDown(args), RoutingStrategies.Tunnel);
            Window.Show();
        }
        internal async Task Frame() { using (await AvatarUiTestDriver.Frame(Window)) { } }
        internal async Task Edit(string text)
        {
            if (text.Length > 0) { await AvatarUiTestDriver.Edit(Window, Editor, text); return; }
            await AvatarUiTestDriver.Click(Window, Editor);
            AvatarUiTestDriver.Press(Window, PhysicalKey.A, RawInputModifiers.Control);
            AvatarUiTestDriver.Press(Window, PhysicalKey.Backspace);
            Assert.That(Editor.Text, Is.Null.Or.Empty, "The native empty TextBox can represent its value as null.");
        }
        internal async Task Load(params string[] words)
        { Controller.Refresh(_ => Words(words)); await Controller.Completion.WaitAsync(Limit); }
        public async ValueTask DisposeAsync()
        { await Controller.BeginClose().WaitAsync(Limit); View.Dispose(); Window.Close(); }
    }
}
