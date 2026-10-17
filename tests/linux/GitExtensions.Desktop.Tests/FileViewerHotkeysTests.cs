using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileViewerHotkeysTests
{
    private const string Patch = "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1,2 +1,2 @@\n-old\n+new\n context\n@@ -10 +10 @@\n-before\n+after\n";

    [AvaloniaTest]
    public async Task RealFindAndNextRemapsRetireOldKeysAndLiveSaveUpdatesExistingMenus()
    {
        using Fixture fixture = new("needle one needle two");
        fixture.Set("Find", new(Key.J, KeyModifiers.Control));
        fixture.Set("FindNextOrOpenWithDifftool", new(Key.F6));
        MenuItem find = Item(fixture, "DiffViewerFind");
        Assert.That(find.InputGesture, Is.EqualTo(new KeyGesture(Key.J, KeyModifiers.Control)));
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(fixture.Window.OwnedWindows.Any(window => window.IsVisible), Is.False);
        Press(fixture.Window, PhysicalKey.J, RawInputModifiers.Control);
        Window dialog = fixture.Window.OwnedWindows.Single(window => window.IsVisible && window.Name == "DiffViewerFindDialog");
        Assert.That(Node<TextBox>(dialog, "DiffViewerFindQuery").Text, Is.EqualTo("needle"));
        Press(dialog!, PhysicalKey.Escape);
        fixture.View.Select(0, 0);
        Press(fixture.Window, PhysicalKey.F3);
        Assert.That(fixture.View.SelectionLength, Is.Zero, "The former F3 binding must not remain an implicit alias.");
        Press(fixture.Window, PhysicalKey.F6); await fixture.View.Search.PendingOperation;
        Assert.That(fixture.View.SelectionStart, Is.Zero); Assert.That(fixture.View.SelectionLength, Is.EqualTo(6));
        Press(fixture.Window, PhysicalKey.F6); await fixture.View.Search.PendingOperation;
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(11));
        fixture.Set("Find", new(Key.K, KeyModifiers.Control));
        Assert.That(find.InputGesture, Is.EqualTo(new KeyGesture(Key.K, KeyModifiers.Control)), "An existing menu follows real profile Save.");
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.J, RawInputModifiers.Control);
        Assert.That(dialog.IsVisible, Is.False);
        Press(fixture.Window, PhysicalKey.K, RawInputModifiers.Control); Assert.That(dialog.IsVisible, Is.True);
        Press(dialog, PhysicalKey.Escape);
        fixture.Set("Find", null); Assert.That(find.InputGesture, Is.Null);
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.K, RawInputModifiers.Control);
        Assert.That(dialog.IsVisible, Is.False, "None must not restore the default or retain the previous assignment.");
    }

    [AvaloniaTest]
    public async Task RemappedForwardFindOpensDifftoolOnlyWithEmptyQueryAndBackwardRemainsSearch()
    {
        using Fixture fixture = new("needle one needle two");
        fixture.Set("FindNextOrOpenWithDifftool", new(Key.F6));
        int difftools = 0;
        fixture.View.Search.OpenDiffToolRequested = () => { difftools++; return Task.CompletedTask; };
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.F6); await fixture.View.Search.PendingOperation;
        Assert.That(difftools, Is.EqualTo(1));
        fixture.View.Search.Query = "needle"; fixture.View.Select(0, 0);
        Press(fixture.Window, PhysicalKey.F6); await fixture.View.Search.PendingOperation;
        Assert.That(fixture.View.SelectionStart, Is.Zero); Assert.That(fixture.View.SelectionLength, Is.EqualTo(6));
        Assert.That(difftools, Is.EqualTo(1));
        fixture.View.Search.Query = ""; fixture.View.Select(0, 0);
        Press(fixture.Window, PhysicalKey.F3, RawInputModifiers.Shift);
        await Until(() => fixture.Window.OwnedWindows.Any(window => window.Name == "DiffViewerSearchInformation" && window.IsVisible), fixture.Window);
        Window message = fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerSearchInformation" && window.IsVisible);
        Assert.That(message.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "No string specified to look for!"), Is.True);
        Click(message.GetVisualDescendants().OfType<Button>().Single());
        await fixture.View.Search.PendingOperation;
        Assert.That(difftools, Is.EqualTo(1), "Backward search never invokes the forward-only external action.");
    }

    [AvaloniaTest]
    public async Task GoToLineRemapResolvesBeforeHostCtrlGAndOldCtrlGReturnsToTheHost()
    {
        using Fixture fixture = new("first\nsecond\nthird\n");
        int hostTerminal = 0;
        fixture.Window.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (fixture.View.IsKeyboardFocusWithin && fixture.View.HandleReadingShortcut(args)) { return; }
            if (!args.Handled && args.Key == Key.G && args.KeyModifiers == KeyModifiers.Control) { args.Handled = true; hostTerminal++; }
        }, RoutingStrategies.Tunnel);
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.G, RawInputModifiers.Control);
        Window original = await GoToWindow(fixture);
        Assert.That(hostTerminal, Is.Zero);
        Click(Node<Button>(original, "DiffViewerGoToCancel")); await fixture.View.ReadingMenu.PendingOperation;
        fixture.Set("GoToLine", new(Key.L, KeyModifiers.Control));
        Assert.That(Item(fixture, "DiffViewerGoToLine").InputGesture, Is.EqualTo(new KeyGesture(Key.L, KeyModifiers.Control)));
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.G, RawInputModifiers.Control);
        Assert.That(hostTerminal, Is.EqualTo(1)); Assert.That(fixture.Window.OwnedWindows.Any(window => window.IsVisible), Is.False);
        Press(fixture.Window, PhysicalKey.L, RawInputModifiers.Control);
        Window remapped = await GoToWindow(fixture);
        TextBox input = Node<NumericUpDown>(remapped, "DiffViewerLineNumber").GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(input.IsKeyboardFocusWithin, Is.True);
        Press(remapped, PhysicalKey.A, RawInputModifiers.Control); remapped.KeyTextInput("3"); Pump(remapped);
        Click(Node<Button>(remapped, "DiffViewerGoToAccept")); await fixture.View.ReadingMenu.PendingOperation;
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(3)); Assert.That(hostTerminal, Is.EqualTo(1));
        Assert.That(fixture.View.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void RemappedReplaceIsConsumedReadOnlyAndOpensTheRealEditableMode(bool editable)
    {
        using Fixture fixture = new("alpha beta", editable: editable);
        fixture.Set("Replace", new(Key.R, KeyModifiers.Control));
        bool? handled = null;
        fixture.Window.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        { if (args.Key == Key.R && args.KeyModifiers == KeyModifiers.Control) { handled = args.Handled; } }, RoutingStrategies.Bubble, handledEventsToo: true);
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.R, RawInputModifiers.Control);
        Assert.That(handled, Is.True);
        Assert.That(fixture.View.Text, Is.EqualTo("alpha beta"));
        Window? dialog = fixture.Window.OwnedWindows.SingleOrDefault(window => window.IsVisible);
        if (!editable) { Assert.That(dialog, Is.Null); return; }
        Assert.That(dialog?.Name, Is.EqualTo("DiffViewerFindDialog"));
        Assert.That(dialog!.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "DiffViewerReplaceWith" && box.IsEffectivelyVisible), Is.True);
        Press(dialog!, PhysicalKey.Escape);
    }

    [AvaloniaTest]
    public void OneExactBindingWinsAcrossSearchAndAppearanceWithoutOldKeyAliases()
    {
        using Fixture fixture = new(Patch, patch: true);
        fixture.Set("Find", new(Key.D, KeyModifiers.Control));
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.D, RawInputModifiers.Control);
        Window find = fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog" && window.IsVisible);
        Assert.That(fixture.View.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch), "Find is first in the original context order, even though appearance is an earlier routed handler.");
        Press(find, PhysicalKey.Escape);
        fixture.Set("Find", null);
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.D, RawInputModifiers.Control);
        Assert.That(fixture.View.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
        fixture.Set("ShowGitWordColoring", new(Key.J, KeyModifiers.Control));
        Press(fixture.Window, PhysicalKey.D, RawInputModifiers.Control);
        Assert.That(fixture.View.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.GitWordDiff));
        Press(fixture.Window, PhysicalKey.J, RawInputModifiers.Control);
        Assert.That(fixture.View.AppearanceMenu.Preferences.Appearance, Is.EqualTo(DiffViewerAppearance.Patch));
    }

    [AvaloniaTest]
    public async Task NavigationAndComparisonRemapsShareExistingActionsAndLiveToolbarHints()
    {
        using Fixture fixture = new(Patch, patch: true);
        fixture.Set("NextChange", new(Key.F7));
        fixture.Set("IncreaseNumberOfVisibleLines", new(Key.F8));
        fixture.Set("ShowSyntaxHighlighting", new(Key.F9));
        int next = 0; fixture.View.Toolbar.NextChangeRequested = () => { next++; return Task.CompletedTask; };
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Assert.That(next, Is.Zero);
        Press(fixture.Window, PhysicalKey.F7); await fixture.View.Toolbar.PendingNavigationOperation;
        Assert.That(next, Is.EqualTo(1));
        Assert.That(ToolTip.GetTip(fixture.View.Toolbar.NextChange)?.ToString(), Does.Contain("F7"));
        int context = fixture.View.ComparisonMenu.Options.ContextLines;
        Press(fixture.Window, PhysicalKey.F8); Assert.That(fixture.View.ComparisonMenu.Options.ContextLines, Is.EqualTo(context + 1));
        bool syntax = fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff;
        Press(fixture.Window, PhysicalKey.F9); Assert.That(fixture.View.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.Not.EqualTo(syntax));
        fixture.Set("NextChange", null);
        Assert.That(ToolTip.GetTip(fixture.View.Toolbar.NextChange)?.ToString(), Does.Not.Contain("F7"));
        Press(fixture.Window, PhysicalKey.F7); Assert.That(next, Is.EqualTo(1));
        fixture.View.ComparisonMenu.SetHostBusy(true);
        Press(fixture.Window, PhysicalKey.F8); Assert.That(fixture.View.ComparisonMenu.Options.ContextLines, Is.EqualTo(context + 1));
    }

    [AvaloniaTest]
    public void RepositoryCommandsRequireAnAvailableHostAndEquivalentReadonlyPatch()
    {
        using Fixture fixture = new(Patch, patch: true);
        List<string> commands = [];
        bool available = false;
        fixture.View.FileViewerCommandRequested = name => { commands.Add(name); return available; };
        fixture.Set("TreatFileAsText", new(Key.F10));
        fixture.View.FocusEditor();
        Press(fixture.Window, PhysicalKey.S); Assert.That(commands, Is.EqualTo(new[] { "StageLines" }));
        available = true; Press(fixture.Window, PhysicalKey.U); Press(fixture.Window, PhysicalKey.R); Press(fixture.Window, PhysicalKey.F10);
        Assert.That(commands, Is.EqualTo(new[] { "StageLines", "UnstageLines", "ResetLines", "TreatFileAsText" }));
        fixture.View.FileViewerCommandRequested = null;
        fixture.View.Mode = DiffViewMode.PlainText; fixture.View.IsReadOnly = false;
        fixture.View.FocusEditor(); fixture.Window.KeyTextInput("su"); Pump(fixture.Window);
        Assert.That(fixture.View.Text, Does.Contain("su"));
        Assert.That(commands, Has.Count.EqualTo(4));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task StandardCopySelectionAndWholeLineRemainEditorCommandsAfterRemaps(bool editable)
    {
        using Fixture fixture = new("first\nsecond\n", editable: editable);
        fixture.Set("Find", new(Key.J, KeyModifiers.Control));
        IClipboard clipboard = fixture.Window.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            fixture.View.FocusEditor(); fixture.View.Select(6, 6);
            Press(fixture.Window, PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("second"));
            fixture.View.Select(6, 0); Press(fixture.Window, PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("second\n"));
            Assert.That(fixture.View.Text, Is.EqualTo("first\nsecond\n"));
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    private static MenuItem Item(Fixture fixture, string name)
        => fixture.View.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static T Node<T>(Window root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(node => node.Name == name);
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); root.CaptureRenderedFrame()?.Dispose(); } Dispatcher.UIThread.RunJobs(); }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered root."); Pump(root);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Height > 0, Is.True);
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static async Task Until(Func<bool> predicate, TopLevel root)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(root); await Task.Delay(10); }
        Assert.That(predicate(), Is.True);
    }
    private static async Task<Window> GoToWindow(Fixture fixture)
    {
        await Until(() => fixture.Window.OwnedWindows.Any(window => window.Name == "DiffViewerGoToLineDialog" && window.IsVisible), fixture.Window);
        return fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerGoToLineDialog" && window.IsVisible);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _oldProfile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
        private readonly CultureInfo _oldCulture = CultureInfo.CurrentUICulture;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gitextensions-fileviewer-hotkeys-" + Guid.NewGuid().ToString("N"));
        private readonly HotkeyBindings _bindings = new();
        private readonly HotkeyPreferencesStore _hotkeys;
        internal DiffView View { get; }
        internal Window Window { get; }
        internal Fixture(string text, bool editable = false, bool patch = false)
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            _hotkeys = new(_directory); _hotkeys.Save(_bindings);
            View = new(new TextBox { Text = text }, new DiffViewerPreferencesStore(_directory))
                { Mode = patch ? DiffViewMode.Unified : DiffViewMode.PlainText };
            if (editable) { View.IsReadOnly = false; }
            Window = new() { Width = 840, Height = 420, Content = View }; Window.Show(); Pump(Window);
        }
        internal void Set(string command, KeyGesture? gesture)
        {
            _bindings.Set("FileViewer", HotkeyCatalog.Find("FileViewer", command)!.Code, gesture);
            _hotkeys.Save(_bindings); Pump(Window);
        }
        public void Dispose()
        {
            try { View.Dispose(); Window.Close(); Pump(Window); }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldProfile.Length == 0 ? null : _oldProfile);
                CultureInfo.CurrentUICulture = _oldCulture;
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
