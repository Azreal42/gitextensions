using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FormEditDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalViewerFillsIts733By571WindowAndEditsUndoAndRedoOnlyInMemory(string language, bool dark)
    {
        await using Host host = new(language, dark);
        string filename = host.File("original.cs"); const string disk = "disk content must remain unchanged\n";
        File.WriteAllText(filename, disk); byte[] before = File.ReadAllBytes(filename);
        const string original = "int original = 1;\r\n// Café λ\n";
        (TextEditDialog dialog, Task done) = host.Open(original, filename);
        Pump(dialog); TextEditor editor = Editor(dialog);
        Assert.That(dialog.Title, Is.EqualTo(host.Translations.Translate("FormEdit", "$this", "Text", "View")));
        Assert.That(dialog.ClientSize.Width, Is.EqualTo(733)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(571));
        Assert.That(dialog.Content, Is.SameAs(dialog.Viewer)); Assert.That(dialog.Viewer.Bounds.Width, Is.EqualTo(dialog.ClientSize.Width).Within(1));
        Assert.That(dialog.Viewer.Bounds.Height, Is.EqualTo(dialog.ClientSize.Height).Within(1));
        Assert.That(dialog.Owner, Is.SameAs(host.Owner)); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(host.Owner.ActualThemeVariant));
        Assert.That(editor.TextArea.IsKeyboardFocusWithin, Is.True); Assert.That(dialog.IsReadOnly, Is.False);
        Assert.That(editor.Text, Is.EqualTo(original)); Assert.That(dialog.Viewer.TextView.SyntaxPath, Is.EqualTo(filename));
        Capture(dialog, "form-edit-" + language + (dark ? "-dark.png" : "-light.png"));
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("edited λ"); Pump(dialog);
        Assert.That(editor.Text, Is.EqualTo("edited λ")); Assert.That(dialog.Viewer.TextView.CanonicalText, Is.EqualTo("edited λ"));
        Press(dialog, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(editor.Text, Is.EqualTo(original));
        Press(dialog, PhysicalKey.Y, RawInputModifiers.Control); Assert.That(editor.Text, Is.EqualTo("edited λ"));
        Press(dialog, PhysicalKey.S, RawInputModifiers.Control); Assert.That(done.IsCompleted, Is.False);
        Assert.That(File.ReadAllBytes(filename), Is.EqualTo(before), "A filename is syntax context, never an implicit write destination.");
        AssertFirstEscapeOnlyClearsSelection(dialog); Assert.That(done.IsCompleted, Is.False);
        Press(dialog, PhysicalKey.Escape); await Until(() => done.IsCompleted); await done.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(File.ReadAllBytes(filename), Is.EqualTo(before)); Assert.That(host.Owner.IsVisible, Is.True);
        Control? returnedFocus = host.Owner.FocusManager?.GetFocusedElement() as Control;
        Assert.That(returnedFocus is not null && TopLevel.GetTopLevel(returnedFocus) == host.Owner, Is.True);
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task ReadOnlyStillSelectsAndCopiesLiteralTextButTypingCannotChangeIt(string language, bool dark)
    {
        await using Host host = new(language, dark);
        const string original = "first line\nCafé € λ\nlast line\n";
        (TextEditDialog dialog, Task done) = host.Open(original, "report.txt", readOnly: true); TextEditor editor = Editor(dialog);
        Assert.That(editor.TextArea.IsKeyboardFocusWithin, Is.True); Assert.That(editor.IsReadOnly, Is.True);
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Assert.That(editor.SelectedText, Is.EqualTo(original));
        var clipboard = dialog.Clipboard!; string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            Press(dialog, PhysicalKey.C, RawInputModifiers.Control);
            await Until(async () => await ClipboardExtensions.TryGetTextAsync(clipboard) == original);
            dialog.KeyTextInput("must not replace selection"); Press(dialog, PhysicalKey.Delete); Pump(dialog);
            Assert.That(editor.Text, Is.EqualTo(original)); Assert.That(dialog.Viewer.TextView.CanonicalText, Is.EqualTo(original));
            Assert.That(done.IsCompleted, Is.False);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
        AssertFirstEscapeOnlyClearsSelection(dialog); Assert.That(done.IsCompleted, Is.False);
        Press(dialog, PhysicalKey.Escape); await Until(() => done.IsCompleted); await done.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public async Task IsReadOnlyCanBeChangedOnTheMappedViewerWithoutReplacingItsDocumentOrLosingUndo()
    {
        await using Host host = new(); (TextEditDialog dialog, Task done) = host.Open("original\n", "note.txt");
        TextEditor editor = Editor(dialog); var document = editor.Document;
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("editable draft"); Pump(dialog);
        dialog.IsReadOnly = true; dialog.KeyTextInput("blocked"); Press(dialog, PhysicalKey.Backspace);
        Assert.That(editor.Text, Is.EqualTo("editable draft")); Assert.That(editor.Document, Is.SameAs(document));
        dialog.IsReadOnly = false; Press(dialog, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.That(editor.Text, Is.EqualTo("original\n")); Assert.That(editor.Document, Is.SameAs(document));
        AssertFirstEscapeOnlyClearsSelection(dialog); Assert.That(done.IsCompleted, Is.False);
        Press(dialog, PhysicalKey.Escape); await Until(() => done.IsCompleted); await done.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public async Task ClosingTheActualOwnerClosesTheViewerWithoutAnInputResultOrFilePublication()
    {
        await using Host host = new(); string filename = host.File("owned.txt"); File.WriteAllText(filename, "published\n");
        (TextEditDialog dialog, Task done) = host.Open("unsaved\n", filename);
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("discard this"); Pump(dialog);
        host.Owner.Close(); await Until(() => done.IsCompleted && !dialog.IsVisible); await done.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(File.ReadAllText(filename), Is.EqualTo("published\n")); Assert.That(host.Owner.IsVisible, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task RealLostObjectQuickViewUsesReadOnlyFormEditAndBothClosePathsLeaveRepositoryBytesUntouched(bool closeRecoveryOwner)
    {
        using ReferenceFixture git = await ReferenceFixture.CreateAsync(); git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0");
        await git.ChangeAsync("lost body λ\n", "Quick view only"); string lost = (await git.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        await git.GitAsync("reset", "--hard", "HEAD~1");
        await using Host host = new(); RecoverLostObjectsDialog recovery = new(git.Repository); host.Own(recovery);
        await recovery.InitializeAsync(); Assert.That(recovery.LastError, Is.Null, recovery.LastException?.ToString());
        Task recoveryDone = recovery.ShowDialog(host.Owner); Pump(recovery);
        ListBox rows = Field<ListBox>(recovery, "RecoveryObjects");
        rows.SelectedIndex = Enumerable.Range(0, recovery.VisibleObjects.Count).Single(i => recovery.VisibleObjects[i].Hash == lost);
        rows.ScrollIntoView(rows.SelectedItem!);
        await Until(() => !recovery.IsBusy && recovery.SelectedObject?.Hash == lost && recovery.Preview?.Hash == lost, () => RecoveryDiagnostic(recovery, rows));
        Pump(recovery); Assert.That(recovery.Preview!.ReadError, Is.Null, RecoveryDiagnostic(recovery, rows));
        Assert.That(Field<FileHistoryContentView>(recovery, "RecoveryPreview").TextView.DocumentIdentity, Is.EqualTo(git.Repository.Path + "\nobject:" + lost));
        string before = await Witness(git); Assert.That(rows.Focus(), Is.True); Pump(recovery);
        Assert.That(rows.IsKeyboardFocusWithin, Is.True, RecoveryDiagnostic(recovery, rows));
        Press(recovery, PhysicalKey.Enter);
        await Until(() => recovery.LastException is not null || recovery.LastError is not null
            || recovery.OwnedWindows.OfType<TextEditDialog>().Any(window => window.IsVisible), () => RecoveryDiagnostic(recovery, rows));
        Assert.That(recovery.LastException, Is.Null, RecoveryDiagnostic(recovery, rows));
        Assert.That(recovery.LastError, Is.Null, RecoveryDiagnostic(recovery, rows));
        TextEditDialog quick = recovery.OwnedWindows.OfType<TextEditDialog>().Single(window => window.IsVisible); Pump(quick);
        Assert.That(quick.Name, Is.EqualTo("RecoveryQuickView")); Assert.That(quick.Owner, Is.SameAs(recovery));
        Assert.That(quick.ClientSize.Width, Is.EqualTo(733)); Assert.That(quick.ClientSize.Height, Is.EqualTo(571));
        Assert.That(quick.Title, Is.EqualTo(host.Translations.Translate("FormEdit", "$this", "Text", "View")));
        Assert.That(quick.IsReadOnly, Is.True); Assert.That(Field<FileHistoryContentView>(quick, "RecoveryQuickViewer"), Is.SameAs(quick.Viewer));
        Assert.That(quick.Viewer.TextView.DocumentIdentity, Is.EqualTo(git.Repository.Path + "\nobject:" + lost));
        Assert.That(quick.Viewer.TextView.CanonicalText, Does.Contain("Quick view only"));
        string displayed = quick.Viewer.TextView.CanonicalText;
        quick.KeyTextInput("must not edit recovered object"); Pump(quick); Assert.That(quick.Viewer.TextView.CanonicalText, Is.EqualTo(displayed));
        if (closeRecoveryOwner)
        {
            recovery.Close(); await Until(() => recoveryDone.IsCompleted && !quick.IsVisible && !recovery.IsBusy);
            await recoveryDone.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            AssertFirstEscapeOnlyClearsSelection(quick); Assert.That(recovery.IsBusy, Is.True);
            Press(quick, PhysicalKey.Escape); await Until(() => !quick.IsVisible && !recovery.IsBusy, () => RecoveryDiagnostic(recovery, rows)); Pump(recovery);
            Assert.That(rows.IsKeyboardFocusWithin, Is.True, "The actual Recovery workflow restores focus to the object list.");
            recovery.Close(); await Until(() => recoveryDone.IsCompleted); await recoveryDone.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(await Witness(git), Is.EqualTo(before), "Viewing and closing a recovered object must not alter HEAD, index, refs or working files.");
    }

    private static async Task<string> Witness(ReferenceFixture git)
    {
        string refs = (await git.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string root = git.Repository.Path;
        return JsonSerializer.Serialize(new { refs, head = Hash(git.PathInRepository(".git/HEAD")), index = Hash(git.PathInRepository(".git/index")),
            config = Hash(git.PathInRepository(".git/config")), files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(git.PathInRepository(".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(root, path), Hash) });
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly List<Window> _windows = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ge-form-edit-" + Guid.NewGuid().ToString("N"));
        internal Window Owner { get; }
        internal TranslationCatalog Translations { get; }
        internal Host(string language = "en", bool dark = false)
        {
            Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
            Translations = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            Owner = new() { Width = 1000, Height = 750, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Content = new TextBox { Text = "Owner focus" } };
            Owner.Show(); ((TextBox)Owner.Content!).Focus(); Pump(Owner);
        }
        internal string File(string name) => Path.Combine(_root, name);
        internal void Own(Window window) => _windows.Add(window);
        internal (TextEditDialog Dialog, Task Done) Open(string text, string filename, bool readOnly = false)
        {
            TextEditDialog dialog = new(text, filename, Translations) { IsReadOnly = readOnly }; Own(dialog);
            Task done = dialog.ShowDialog(Owner); Pump(dialog); return (dialog, done);
        }
        public async ValueTask DisposeAsync()
        {
            static void CloseChildren(Window parent)
            { foreach (Window child in parent.OwnedWindows.ToArray()) { CloseChildren(child); child.Close(); } }
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse()) { CloseChildren(window); if (window.IsVisible) { window.Close(); } }
                await Until(() => _windows.OfType<RepositoryDialog>().All(dialog => !dialog.IsBusy));
                foreach (Window window in _windows) { if (window.IsVisible) { window.Close(); } if (window is TextEditDialog text) { text.Dispose(); } }
                CloseChildren(Owner); Owner.Close(); Dispatcher.UIThread.RunJobs();
            }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
        }
    }
    private static void AssertFirstEscapeOnlyClearsSelection(TextEditDialog dialog)
    {
        TextEditor editor = Editor(dialog); string text = editor.Text;
        Assert.That(editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Assert.That(editor.SelectionLength, Is.GreaterThan(0));
        Press(dialog, PhysicalKey.Escape);
        Assert.That(editor.SelectionLength, Is.Zero, "FileViewer consumes the first Escape to clear its actual selection.");
        Assert.That(editor.Text, Is.EqualTo(text)); Assert.That(dialog.IsVisible, Is.True);
    }
    private static string RecoveryDiagnostic(RecoverLostObjectsDialog dialog, ListBox rows)
    {
        Control? focused = dialog.FocusManager?.GetFocusedElement() as Control;
        return "busy=" + dialog.IsBusy + "; LastError=" + dialog.LastError + "; LastException=" + dialog.LastException
            + "; selected=" + dialog.SelectedObject?.Hash + "; preview=" + dialog.Preview?.Hash + "; previewError=" + dialog.Preview?.ReadError
            + "; focused=" + focused + "; focusOwner=" + (focused is null ? null : TopLevel.GetTopLevel(focused)?.Name)
            + "; listFocus=" + rows.IsKeyboardFocusWithin + "; enabled=" + rows.IsEffectivelyEnabled + "; visible=" + rows.IsEffectivelyVisible
            + "; menu=" + rows.ContextMenu?.IsOpen + "; owned=" + string.Join(", ", dialog.OwnedWindows.Select(window => window.Name + ":visible=" + window.IsVisible));
    }
    private static TextEditor Editor(TextEditDialog dialog) => dialog.Viewer.GetVisualDescendants().OfType<TextEditor>().Single();
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { Pump(window); } }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> complete, Func<string>? diagnostic = null)
    {
        Stopwatch timer = Stopwatch.StartNew(); while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(complete(), Is.True, "The actual FormEdit or Recovery operation did not settle. " + diagnostic?.Invoke());
    }
    private static async Task Until(Func<Task<bool>> complete)
    {
        Stopwatch timer = Stopwatch.StartNew(); bool ready;
        while (!(ready = await complete()) && timer.Elapsed < TimeSpan.FromSeconds(5)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(ready, Is.True, "The actual clipboard command did not settle.");
    }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}
