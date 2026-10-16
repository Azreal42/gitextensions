using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class EditableFileViewTests
{
    [AvaloniaTest]
    public void ExistingFileViewerRemainsReadOnlyAndHasNoReplaceOperation()
    {
        using Fixture fixture = new("alpha β🙂", editable: false);
        fixture.View.FocusEditor(); fixture.Window.KeyTextInput("never"); Press(fixture.Window, PhysicalKey.H, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo("alpha β🙂"));
        Assert.That(fixture.View.IsReadOnly, Is.True);
        Assert.That(fixture.Window.OwnedWindows, Is.Empty);
        fixture.View.Search.Query = "alpha"; fixture.View.Search.Replacement = "beta"; fixture.View.Select(0, 5);
        Assert.That(fixture.View.Search.ReplaceNext(), Is.Null);
        Assert.That(fixture.View.Search.ReplaceAll(), Is.Zero);
        Assert.That(fixture.View.Text, Is.EqualTo("alpha β🙂"));
        Assert.That(fixture.View.CreateContextMenuItems().OfType<MenuItem>().Single(item => item.Name == "DiffViewerReplace").IsVisible, Is.False);
        fixture.View.Mode = DiffViewMode.Unified;
        Assert.Throws<InvalidOperationException>(() => fixture.View.IsReadOnly = false);
    }

    [AvaloniaTest]
    public void NativeTypingUndoRedoAndDirtyBaselineKeepTheSameEditableDocument()
    {
        const string original = "alpha\r\nβ🙂\r\n";
        using Fixture fixture = new(original);
        var document = fixture.View.Editor.Document;
        int changes = 0, modified = 0;
        fixture.View.TextChanged += (_, _) => changes++;
        fixture.View.ModifiedChanged += (_, _) => modified++;
        fixture.View.FocusEditor(); fixture.View.Select(0, 5); fixture.Window.KeyTextInput("new"); Pump(fixture.Window);
        Assert.That(fixture.View.Text, Is.EqualTo("new\r\nβ🙂\r\n"));
        Assert.That(fixture.Source.Text, Is.EqualTo(fixture.View.Text));
        Assert.That(fixture.View.Editor.Document, Is.SameAs(document));
        Assert.That(fixture.View.IsModified, Is.True); Assert.That(fixture.View.CaretOffset, Is.EqualTo(3));
        Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo(original)); Assert.That(fixture.View.IsModified, Is.False);
        Press(fixture.Window, PhysicalKey.Y, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo("new\r\nβ🙂\r\n")); Assert.That(fixture.View.IsModified, Is.True);
        fixture.View.AcceptChanges(); Assert.That(fixture.View.IsModified, Is.False);
        Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo(original)); Assert.That(fixture.View.IsModified, Is.True, "Undo can move behind the saved version without discarding history.");
        Press(fixture.Window, PhysicalKey.Y, RawInputModifiers.Control);
        Assert.That(fixture.View.IsModified, Is.False);
        fixture.View.Select(0, 3); fixture.Window.KeyTextInput("new"); Pump(fixture.Window);
        Assert.That(fixture.View.IsModified, Is.False, "Replacing saved text by equal text must not create a dirty file.");
        Assert.That(changes, Is.GreaterThanOrEqualTo(5)); Assert.That(modified, Is.GreaterThanOrEqualTo(5));
    }

    [AvaloniaTest]
    public async Task NativeCutCopyPasteAndSelectAllUseTheSharedEditorClipboard()
    {
        using Fixture fixture = new("alpha β🙂\r\nlast");
        IClipboard clipboard = fixture.Window.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            fixture.View.FocusEditor(); fixture.View.Select(6, 3);
            Press(fixture.Window, PhysicalKey.C, RawInputModifiers.Control);
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("β🙂"));
            Press(fixture.Window, PhysicalKey.X, RawInputModifiers.Control);
            Assert.That(fixture.View.Text, Is.EqualTo("alpha \r\nlast"));
            Press(fixture.Window, PhysicalKey.V, RawInputModifiers.Control);
            await UntilAsync(() => fixture.View.Text.Contains("β🙂", StringComparison.Ordinal), fixture.Window);
            Assert.That(fixture.View.Text, Is.EqualTo("alpha β🙂\r\nlast"));
            Assert.That(fixture.View.IsModified, Is.False);
            await ClipboardExtensions.SetTextAsync(clipboard, "# local\n*.tmp\n");
            Press(fixture.Window, PhysicalKey.A, RawInputModifiers.Control);
            Assert.That(fixture.View.SelectionLength, Is.EqualTo(fixture.View.Text.Length));
            Press(fixture.Window, PhysicalKey.V, RawInputModifiers.Control);
            await UntilAsync(() => fixture.View.Text.StartsWith("# local", StringComparison.Ordinal), fixture.Window);
            Assert.That(fixture.View.Text, Is.EqualTo("# local\r\n*.tmp\r\n"), "Native paste follows the loaded file's CRLF convention.");
            Assert.That(fixture.Source.Text, Is.EqualTo(fixture.View.Text));
            Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
            Assert.That(fixture.View.Text, Is.EqualTo("alpha β🙂\r\nlast"));
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public void PlainXIsTextAndIsNotTheReadOnlySyntaxToggleShortcut()
    {
        using Fixture fixture = new("");
        fixture.View.FocusEditor();
        KeyEventArgs key = new() { Key = Key.X, RoutedEvent = InputElement.KeyDownEvent };
        fixture.View.Editor.TextArea.RaiseEvent(key);
        Assert.That(key.Handled, Is.False, "The read-only X shortcut must leave ordinary editor input available.");
        fixture.Window.KeyTextInput("x"); Pump(fixture.Window);
        Assert.That(fixture.View.Text, Is.EqualTo("x"));
    }

    [AvaloniaTest]
    public void NativeEnterAndBackspaceUpdatePlainFileNumbersWithoutReloadingTheEditor()
    {
        using Fixture fixture = new("first\r\nsecond");
        var document = fixture.View.Editor.Document;
        fixture.View.FocusEditor(); fixture.View.Select(fixture.View.Text.Length, 0);
        Press(fixture.Window, PhysicalKey.Enter); fixture.Window.KeyTextInput("third"); Pump(fixture.Window);
        Assert.That(fixture.View.Text, Is.EqualTo("first\r\nsecond\r\nthird"));
        Assert.That(fixture.View.Editor.Document.LineCount, Is.EqualTo(3));
        Assert.That(fixture.View.GetLineNumbers(3), Is.EqualTo(((int?)null, (int?)3)));
        fixture.View.Select("first\r\nsecond\r\n".Length, 0); Press(fixture.Window, PhysicalKey.Backspace);
        Assert.That(fixture.View.Text, Is.EqualTo("first\r\nsecondthird"));
        Assert.That(fixture.View.Editor.Document.LineCount, Is.EqualTo(2));
        Assert.That(fixture.View.GetLineNumbers(2), Is.EqualTo(((int?)null, (int?)2)));
        Assert.That(fixture.View.Editor.Document, Is.SameAs(document));
        Assert.That(fixture.Source.Text, Is.EqualTo(fixture.View.Text));
    }

    [AvaloniaTest]
    public void PaletteSyntaxNonprintingEncodingAndZoomKeepUnsavedTextSelectionCaretAndUndo()
    {
        using Fixture fixture = new("# original\n*.tmp\n");
        fixture.View.FocusEditor(); fixture.View.Select(fixture.View.Text.Length, 0); fixture.Window.KeyTextInput("α🙂"); Pump(fixture.Window);
        var document = fixture.View.Editor.Document;
        string edited = fixture.View.Text;
        fixture.View.Select(edited.IndexOf("α", StringComparison.Ordinal), 3);
        var selection = (fixture.View.SelectionStart, fixture.View.SelectionLength, fixture.View.CaretOffset);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme;
            fixture.View.SyntaxPath = ".gitignore";
            fixture.View.RenderingMenu.ToggleNonPrinting();
            fixture.Store.Save(fixture.Store.Load() with { UseGitColoring = !fixture.Store.Load().UseGitColoring });
            fixture.View.FileEncodingName = theme == ThemeVariant.Dark ? "windows-1252" : null;
            fixture.View.Editor.FontFamily = new FontFamily("DejaVu Sans Mono");
            fixture.View.Editor.FontSize = 15;
            Pump(fixture.Window);
            Assert.That(fixture.View.Editor.Document, Is.SameAs(document));
            Assert.That(fixture.View.Text, Is.EqualTo(edited)); Assert.That(fixture.Source.Text, Is.EqualTo(edited));
            Assert.That((fixture.View.SelectionStart, fixture.View.SelectionLength, fixture.View.CaretOffset), Is.EqualTo(selection));
            Assert.That(fixture.View.IsModified, Is.True);
        }
        fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo("# original\n*.tmp\n")); Assert.That(fixture.View.IsModified, Is.False);
    }

    [AvaloniaTest]
    public void CtrlHUsesTheSameCachedFindWindowAndReplacesThroughTheActualButtons()
    {
        using Fixture fixture = new("red RED redder red");
        fixture.View.FocusEditor(); fixture.View.Select(0, 3); Press(fixture.Window, PhysicalKey.H, RawInputModifiers.Control);
        Window dialog = FindWindow(fixture);
        TextBox query = Find<TextBox>(dialog, "DiffViewerFindQuery"), replacement = Find<TextBox>(dialog, "DiffViewerReplaceWith");
        Assert.That(query.Text, Is.EqualTo("red")); Assert.That(query.IsKeyboardFocusWithin, Is.True);
        Assert.That(dialog.Title, Is.EqualTo("Find & replace")); Assert.That(replacement.IsEffectivelyVisible, Is.True);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme; Pump(dialog);
            Button replaceAll = Find<Button>(dialog, "DiffViewerReplaceAll");
            Point bottom = replaceAll.TranslatePoint(new Point(replaceAll.Bounds.Width, replaceAll.Bounds.Height), dialog)!.Value;
            Assert.That(bottom.Y, Is.LessThanOrEqualTo(dialog.ClientSize.Height), "The original compact replace footer must fit in both palettes.");
            using var frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("No rendered replace window.");
            string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "file-viewer-replace-" + theme + ".png");
            frame.Save(path); TestContext.AddTestAttachment(path);
        }
        replacement.Text = "blue"; Click(Find<CheckBox>(dialog, "DiffViewerFindMatchCase")); Click(Find<CheckBox>(dialog, "DiffViewerFindMatchWholeWord"));
        Click(Find<Button>(dialog, "DiffViewerReplace"));
        Assert.That(fixture.View.Text, Is.EqualTo("blue RED redder red"));
        Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("red")); Assert.That(fixture.View.SelectionStart, Is.EqualTo(16));
        Press(dialog, PhysicalKey.Escape); Assert.That(fixture.View.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Press(fixture.Window, PhysicalKey.F, RawInputModifiers.Control);
        Assert.That(FindWindow(fixture), Is.SameAs(dialog)); Assert.That(replacement.IsEffectivelyVisible, Is.False);
        Assert.That(Find<Button>(dialog, "DiffViewerReplace").IsEffectivelyVisible, Is.False);
        Press(dialog, PhysicalKey.Escape);
        fixture.View.Select(0, 0); fixture.View.Search.Query = "RED";
        Press(fixture.Window, PhysicalKey.F3); Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("RED"));
    }

    [AvaloniaTest]
    public void ReplaceAllRestrictsTheOriginalMultilineSelectionHandlesGrowingQueryAndIsOneUndo()
    {
        const string original = "red outside\nred\nRED red\nred outside";
        using Fixture fixture = new(original);
        int start = original.IndexOf("red\nRED", StringComparison.Ordinal);
        fixture.View.Search.Query = "red";
        fixture.View.Select(start, "red\nRED red".Length); fixture.View.FocusEditor(); Press(fixture.Window, PhysicalKey.H, RawInputModifiers.Control);
        Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(new DiffViewerSearchMatch(start, "red\nRED red".Length)));
        fixture.View.Search.Replacement = "very red";
        Assert.That(fixture.View.Search.ReplaceAll(), Is.EqualTo(3));
        Assert.That(fixture.View.Text, Is.EqualTo("red outside\nvery red\nvery red very red\nred outside"));
        Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(new DiffViewerSearchMatch(start, "very red\nvery red very red".Length)));
        Assert.That(fixture.View.IsModified, Is.True);
        Press(FindWindow(fixture), PhysicalKey.Escape);
        Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo(original)); Assert.That(fixture.View.IsModified, Is.False);
        Press(fixture.Window, PhysicalKey.Y, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo("red outside\nvery red\nvery red very red\nred outside"));
    }

    [AvaloniaTest]
    public void ReplaceRespectsCaseWholeWordsEmptyReplacementAndEmptyQuery()
    {
        using Fixture fixture = new("name NAME surname name_suffix name\r\nλ🙂 λ🙂");
        fixture.View.Search.Query = "name"; fixture.View.Search.MatchCase = true; fixture.View.Search.MatchWholeWordOnly = true;
        fixture.View.Search.Replacement = "";
        Assert.That(fixture.View.Search.ReplaceAll(), Is.EqualTo(2));
        Assert.That(fixture.View.Text, Is.EqualTo(" NAME surname name_suffix \r\nλ🙂 λ🙂"));
        fixture.View.Search.Query = "λ🙂"; fixture.View.Search.Replacement = "β🙂";
        Assert.That(fixture.View.Search.ReplaceAll(), Is.EqualTo(2));
        Assert.That(fixture.View.Text, Does.EndWith("β🙂 β🙂"));
        fixture.View.Search.Query = "";
        Assert.That(fixture.View.Search.ReplaceAll(), Is.Zero);
    }

    [AvaloniaTest]
    public void ExplicitLoadClearsOldUndoEvenForTheSameSnapshotAndAcceptChangesDoesNot()
    {
        using Fixture fixture = new("old");
        fixture.View.FocusEditor(); fixture.View.Select(3, 0); fixture.Window.KeyTextInput(" edit"); Pump(fixture.Window);
        fixture.View.LoadText(fixture.View.Text);
        Assert.That(fixture.View.IsModified, Is.False); Assert.That(fixture.View.Editor.Document.UndoStack.CanUndo, Is.False);
        fixture.View.Select(fixture.View.Text.Length, 0); fixture.Window.KeyTextInput(" more"); Pump(fixture.Window); fixture.View.AcceptChanges();
        Assert.That(fixture.View.Editor.Document.UndoStack.CanUndo, Is.True);
        Press(fixture.Window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(fixture.View.Text, Is.EqualTo("old edit")); Assert.That(fixture.View.IsModified, Is.True);
    }

    [AvaloniaTest]
    public void HostBusyAndReadOnlyTransitionsRefreshFindReplaceWithoutMovingTheCaret()
    {
        using Fixture fixture = new("text");
        var items = fixture.View.CreateContextMenuItems().OfType<MenuItem>().ToArray();
        MenuItem find = items.Single(item => item.Name == "DiffViewerFind"), replace = items.Single(item => item.Name == "DiffViewerReplace");
        Assert.That(find.IsEnabled && replace.IsEnabled && replace.IsVisible, Is.True);
        fixture.View.IsEnabled = false; Pump(fixture.Window);
        Assert.That(find.IsEnabled || replace.IsEnabled, Is.False);
        fixture.View.IsEnabled = true; Pump(fixture.Window);
        Assert.That(find.IsEnabled && replace.IsEnabled, Is.True);
        fixture.View.IsReadOnly = true; Pump(fixture.Window);
        Assert.That(replace.IsVisible || replace.IsEnabled, Is.False);
        fixture.View.IsReadOnly = false; Pump(fixture.Window);
        Assert.That(replace.IsVisible && replace.IsEnabled, Is.True);
    }

    private static Window FindWindow(Fixture fixture) => fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog" && window.IsVisible);
    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root is not Window { IsVisible: false }) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered root."); Pump(root);
        Assert.That(control.IsEffectivelyVisible && control.Bounds.Height > 0, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static async Task UntilAsync(Func<bool> predicate, TopLevel root)
    {
        DateTime timeout = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout) { Pump(root); await Task.Delay(10); }
        Assert.That(predicate(), Is.True);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-editable-view-" + Guid.NewGuid().ToString("N"));
        internal TextBox Source { get; } = new();
        internal DiffViewerPreferencesStore Store { get; }
        internal DiffView View { get; }
        internal Window Window { get; }
        internal Fixture(string text, bool editable = true)
        {
            Directory.CreateDirectory(_directory); Store = new(_directory);
            View = new(Source, Store) { Mode = DiffViewMode.PlainText, IsReadOnly = !editable };
            View.LoadText(text);
            Window = new() { Width = 720, Height = 320, Content = View };
            Window.Show(); Pump(Window);
        }
        public void Dispose()
        { View.Dispose(); Window.Close(); Pump(Window); Directory.Delete(_directory, recursive: true); }
    }
}
