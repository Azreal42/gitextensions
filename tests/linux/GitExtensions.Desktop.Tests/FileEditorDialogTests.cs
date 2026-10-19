using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileEditorDialogTests
{
    [AvaloniaTest, TestCase("en", false), TestCase("fr", true)]
    public async Task OriginalChromeWarningLineAndExplicitSavePreserveTheDocumentUndoAndExactDiskBytes(string language, bool dark)
    {
        await using Host h = new(language, dark); const string original = "first\r\nCafé λ\nlast"; File.WriteAllText(h.Path, original, new UTF8Encoding(false));
        (FileEditorDialog d, Task done) = h.Open(warning: true, line: 2); await Ready(d);
        Assert.That(d.ClientSize.Width, Is.EqualTo(659)); Assert.That(d.ClientSize.Height, Is.EqualTo(543)); Assert.That(d.Title, Is.EqualTo(h.Path));
        Assert.That(d.Owner, Is.SameAs(h.Owner)); Assert.That(d.ActualThemeVariant, Is.EqualTo(h.Owner.ActualThemeVariant));
        Assert.That(Field<Border>(d, "panelMessage").IsVisible, Is.True);
        Assert.That(Field<TextBlock>(d, "labelWarning").Text, Does.StartWith(language == "fr" ? "Ici vivent les dragons!" : "Here be dragons!"));
        Assert.That(d.Viewer.CurrentFileLine, Is.EqualTo(2)); Assert.That(d.Viewer.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Button save = Field<Button>(d, "toolStripSaveButton"); Assert.That(save.IsEnabled, Is.False);
        var document = d.Viewer.Editor.Document; Capture(d, "file-editor-" + language + (dark ? "-dark.png" : "-light.png"));
        Edit(d, "edited λ\r\nno final newline"); Assert.That(save.IsEnabled, Is.True);
        Press(d, PhysicalKey.S, RawInputModifiers.Control); await Until(() => !d.IsBusy && d.HasFileChanges);
        Assert.That(d.LastError, Is.Null, d.LastException?.ToString()); Assert.That(d.IsVisible, Is.True); Assert.That(d.Viewer.IsModified, Is.False);
        Assert.That(d.Viewer.Editor.Document, Is.SameAs(document)); Assert.That(File.ReadAllBytes(h.Path), Is.EqualTo(Encoding.UTF8.GetBytes("edited λ\r\nno final newline")));
        Press(d, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(d.Viewer.Text, Is.EqualTo(original)); Assert.That(d.Viewer.IsModified, Is.True);
        Press(d, PhysicalKey.Y, RawInputModifiers.Control); Assert.That(d.Viewer.IsModified, Is.False);
        SelectionThenEscape(d); await done.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(d.CloseAccepted, Is.True);
    }
    [AvaloniaTest, TestCase("Yes"), TestCase("No"), TestCase("Cancel")]
    public async Task DirtyCloseUsesActualYesNoCancelChoicesAndOnlyYesWrites(string choice)
    {
        await using Host h = new(); File.WriteAllText(h.Path, "original"); (FileEditorDialog d, Task done) = h.Open(); await Ready(d);
        Edit(d, "draft"); d.Close(); Window q = await Question(d); Click(q, "FileEditorSave" + choice);
        if (choice == "Cancel")
        { await Until(() => !q.IsVisible); Assert.That(d.IsVisible && d.Viewer.IsModified, Is.True); Assert.That(done.IsCompleted, Is.False); Assert.That(File.ReadAllText(h.Path), Is.EqualTo("original")); }
        else
        { await done.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(File.ReadAllText(h.Path), Is.EqualTo(choice == "Yes" ? "draft" : "original")); Assert.That(d.CloseAccepted, Is.EqualTo(choice == "Yes")); }
    }
    [AvaloniaTest]
    public async Task FailedSaveKeepsDirtyTextAndActualCodecAndOriginalBytesUntilTheErrorIsAcknowledged()
    {
        await using Host h = new(); byte[] original = [0x63,0x61,0x66,0xe9]; File.WriteAllBytes(h.Path, original);
        (FileEditorDialog d, _) = h.Open(codec: "windows-1252"); await Ready(d); Edit(d, "cannot encode λ");
        Click(d, "toolStripSaveButton"); Window error = await Message(d);
        Assert.That(d.LastException, Is.TypeOf<EncoderFallbackException>()); Assert.That(d.Viewer.IsModified, Is.True);
        Assert.That(d.Viewer.Text, Is.EqualTo("cannot encode λ")); Assert.That(File.ReadAllBytes(h.Path), Is.EqualTo(original)); Assert.That(d.HasFileChanges, Is.False);
        Click(error, "MessageDialogAccept"); await Until(() => !error.IsVisible); Assert.That(d.IsVisible, Is.True);
    }
    [AvaloniaTest]
    public async Task FailedSaveFromTheDirtyCloseQuestionKeepsTheEditorOpenAndRestoresTheSaveCommand()
    {
        await using Host h = new(); File.WriteAllBytes(h.Path, [0x63, 0x61, 0x66, 0xe9]);
        (FileEditorDialog d, Task done) = h.Open(codec: "windows-1252"); await Ready(d); Edit(d, "λ");
        d.Close(); Window q = await Question(d); Click(q, "FileEditorSaveYes"); Window error = await Message(d);
        Assert.That(d.LastException, Is.TypeOf<EncoderFallbackException>()); Click(error, "MessageDialogAccept");
        await Until(() => !q.IsVisible && !error.IsVisible && Field<Button>(d, "toolStripSaveButton").IsEnabled);
        Assert.That(d.IsVisible && d.Viewer.IsModified, Is.True); Assert.That(done.IsCompleted, Is.False);
        Edit(d, "encodable"); Press(d, PhysicalKey.S, RawInputModifiers.Control); await Until(() => !d.IsBusy && d.HasFileChanges);
        Assert.That(d.LastError, Is.Null); Assert.That(File.ReadAllText(h.Path, Encoding.ASCII), Is.EqualTo("encodable"));
    }
    [AvaloniaTest]
    public async Task ExternalDiskChangesAreNotSilentlyOverwrittenOrUsedToEraseTheDraft()
    {
        await using Host h = new(); File.WriteAllText(h.Path, "loaded"); (FileEditorDialog d, _) = h.Open(); await Ready(d);
        Edit(d, "my draft"); File.WriteAllText(h.Path, "external actor"); Press(d, PhysicalKey.S, RawInputModifiers.Control); Window error = await Message(d);
        Assert.That(d.LastException, Is.InstanceOf<IOException>()); Assert.That(d.Viewer.Text, Is.EqualTo("my draft")); Assert.That(d.Viewer.IsModified, Is.True);
        Assert.That(File.ReadAllText(h.Path), Is.EqualTo("external actor")); Assert.That(d.HasFileChanges, Is.False); Click(error, "MessageDialogAccept");
    }
    [AvaloniaTest]
    public async Task EncodingCancelAndFailedReloadKeepTheSameDirtyDocumentAndUndoWithoutWriting()
    {
        await using Host h = new(); File.WriteAllText(h.Path, "café", new UTF8Encoding(false)); (FileEditorDialog d, _) = h.Open(); await Ready(d);
        var document = d.Viewer.Editor.Document; EditableFileSnapshot baseline = d.Snapshot!; Edit(d, "café draft");
        d.Viewer.FileEncodingName = "us-ascii"; Window q = await Question(d); Click(q, "FileEditorSaveCancel"); await Until(() => !q.IsVisible);
        Assert.That(d.Viewer.FileEncodingName, Is.EqualTo("utf-8")); Assert.That(d.Viewer.Text, Is.EqualTo("café draft"));
        d.Viewer.FileEncodingName = "us-ascii"; q = await Question(d); Click(q, "FileEditorSaveNo"); Window error = await Message(d);
        Assert.That(d.LastException, Is.TypeOf<DecoderFallbackException>()); Assert.That(d.Snapshot, Is.SameAs(baseline)); Assert.That(d.Viewer.Editor.Document, Is.SameAs(document));
        Assert.That(d.Viewer.Text, Is.EqualTo("café draft")); Assert.That(d.Viewer.FileEncodingName, Is.EqualTo("utf-8")); Assert.That(File.ReadAllText(h.Path), Is.EqualTo("café"));
        Click(error, "MessageDialogAccept"); d.Viewer.FocusEditor(); Press(d, PhysicalKey.Z, RawInputModifiers.Control); Assert.That(d.Viewer.Text, Is.EqualTo("café"));
    }
    [AvaloniaTest, TestCase(false), TestCase(true)]
    public async Task ReadonlyOptionAndBinaryInspectionCannotSaveEvenThroughCtrlS(bool binary)
    {
        await using Host h = new(); byte[] bytes = binary ? [0,1,255] : Encoding.UTF8.GetBytes("read only"); File.WriteAllBytes(h.Path, bytes);
        (FileEditorDialog d, _) = h.Open(readOnly: !binary); await Ready(d); Assert.That(d.Viewer.IsReadOnly, Is.True);
        d.KeyTextInput("must not write"); Press(d, PhysicalKey.S, RawInputModifiers.Control);
        Assert.That(Field<Button>(d, "toolStripSaveButton").IsEnabled, Is.False); Assert.That(File.ReadAllBytes(h.Path), Is.EqualTo(bytes)); Assert.That(d.HasFileChanges, Is.False);
    }
    [AvaloniaTest]
    public async Task MissingFileShowsTheActualOpenFailureAndNeverCreatesAnEditableErrorDocument()
    {
        await using Host h = new(); (FileEditorDialog d, Task done) = h.Open(); Window error = await Message(d);
        Assert.That(d.LastException, Is.InstanceOf<FileNotFoundException>()); Assert.That(d.Snapshot, Is.Null); Assert.That(d.Viewer.IsReadOnly, Is.True);
        Click(error, "MessageDialogAccept"); await done.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(File.Exists(h.Path), Is.False); Assert.That(d.CloseAccepted, Is.False);
    }
    [AvaloniaTest]
    public async Task LargeFileRequiresExplicitFullLoadBeforeAnyEditingOrSaveCanStart()
    {
        await using Host h = new(); byte[] bytes = Enumerable.Range(0, checked((int)FileEditorDialog.DeferredLength + 1)).Select(i => (byte)(i % 80 == 79 ? '\n' : 'x')).ToArray(); File.WriteAllBytes(h.Path, bytes);
        (FileEditorDialog d, _) = h.Open(); await d.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(d.Snapshot, Is.Null); Assert.That(Field<Button>(d, "_NO_TRANSLATE_lblShowPreview").IsVisible, Is.True); Assert.That(Field<Button>(d, "toolStripSaveButton").IsEnabled, Is.False);
        Click(d, "_NO_TRANSLATE_lblShowPreview"); await Until(() => !d.IsBusy && d.Snapshot is not null);
        Assert.That(d.Viewer.Text.Length, Is.EqualTo(bytes.Length)); Assert.That(d.Snapshot!.GetBytes(), Is.EqualTo(bytes)); Assert.That(d.Viewer.IsModified, Is.False);
    }
    [AvaloniaTest]
    public async Task FindAndContextMenuConsumeEscapeBeforeSelectionAndTheDirtyCloseQuestion()
    {
        await using Host h = new(); File.WriteAllText(h.Path, "original"); (FileEditorDialog d, _) = h.Open(); await Ready(d); Edit(d, "unsaved");
        Press(d, PhysicalKey.H, RawInputModifiers.Control);
        await Until(() => d.OwnedWindows.Any(w => w.IsVisible && w.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "DiffViewerFindQuery")));
        Window find = d.OwnedWindows.Single(w => w.IsVisible); Press(find, PhysicalKey.Escape);
        Assert.That(d.IsVisible, Is.True); Assert.That(d.OwnedWindows.Any(w => w.Name == "FileEditorSaveQuestion"), Is.False);
        ContextMenu menu = d.Viewer.ContextMenu!; menu.Open(d.Viewer); Pump(d); MenuItem first = menu.Items.OfType<MenuItem>().First(); first.Focus();
        Press(TopLevel.GetTopLevel(first)!, PhysicalKey.Escape); Assert.That(menu.IsOpen, Is.False); Assert.That(d.IsVisible, Is.True);
        d.Viewer.FocusEditor(); SelectionThenEscape(d); Window q = await Question(d); Click(q, "FileEditorSaveCancel"); await Until(() => !q.IsVisible);
        Assert.That(d.IsVisible && d.Viewer.IsModified, Is.True);
    }
    [AvaloniaTest]
    public async Task OwnerCloseCancelStaysOpenThenSaveAndDiscardRetainEarlierDiskEffectsAndReturnToOwner()
    {
        await using Host h = new(); File.WriteAllText(h.Path, "original"); (FileEditorDialog d, Task done) = h.Open(); await Ready(d);
        Edit(d, "first save"); Press(d, PhysicalKey.S, RawInputModifiers.Control); await Until(() => !d.IsBusy && d.HasFileChanges);
        Edit(d, "later unsaved"); h.Owner.Close(); Window q = await Question(d); Click(q, "FileEditorSaveCancel"); await Until(() => !q.IsVisible);
        Assert.That(h.Owner.IsVisible && d.IsVisible, Is.True); h.Owner.Close(); q = await Question(d); Click(q, "FileEditorSaveNo");
        await done.WaitAsync(TimeSpan.FromSeconds(5)); await Until(() => !h.Owner.IsVisible);
        Assert.That(File.ReadAllText(h.Path), Is.EqualTo("first save")); Assert.That(d.HasFileChanges, Is.True); Assert.That(d.CloseAccepted, Is.False);
    }
    [AvaloniaTest, TestCase(false), TestCase(true)]
    public async Task ClosingWaitsForTheActualReadOrSaveCompletionAndRejectsALateReadPublication(bool save)
    {
        await using Host h = new(); File.WriteAllText(h.Path, "original"); HeldStore held = new(save);
        (FileEditorDialog d, Task done) = h.Open(store: held);
        try
        {
            if (save) { await Ready(d); Edit(d, "saved"); Press(d, PhysicalKey.S, RawInputModifiers.Control); }
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); d.Close(); Pump(d); Assert.That(d.IsVisible && d.IsBusy, Is.True); Assert.That(done.IsCompleted, Is.False);
            held.Release.TrySetResult(); await done.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(d.HasFileChanges, Is.EqualTo(save)); Assert.That(File.ReadAllText(h.Path), Is.EqualTo(save ? "saved" : "original"));
            if (!save) { Assert.That(d.Snapshot, Is.Null, "A cancelled uncooperative read must not publish after the close request."); }
        }
        finally { held.Release.TrySetResult(); }
    }
    private sealed class HeldStore(bool holdSave) : IEditableFileStore
    {
        private readonly EditableFileStore _real = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EditableFileSnapshot> ReadAsync(string path, string defaultEncodingName = "utf-8", CancellationToken cancellationToken = default)
        { EditableFileSnapshot snapshot = await _real.ReadAsync(path, defaultEncodingName, cancellationToken); if (!holdSave) { Started.TrySetResult(); await Release.Task; } return snapshot; }
        public async Task<EditableFileSnapshot> SaveAsync(EditableFileSnapshot snapshot, string text, CancellationToken cancellationToken = default)
        { EditableFileSnapshot saved = await _real.SaveAsync(snapshot, text, cancellationToken); Started.TrySetResult(); await Release.Task; return saved; }
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-file-editor-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "literal [1] λ.txt");
        internal Window Owner { get; } = new() { Width = 1000, Height = 750 };
        private readonly List<FileEditorDialog> _dialogs = [];
        private readonly TranslationCatalog _translations;
        internal Host(string language = "en", bool dark = false)
        {
            Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", System.IO.Path.Combine(Root, "config"));
            _translations = TranslationCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
            Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Owner.Show();
        }
        internal (FileEditorDialog Dialog, Task Done) Open(string codec = "utf-8", bool warning = false, bool readOnly = false, int? line = null, IEditableFileStore? store = null)
        {
            FileEditorDialog dialog = new(Path, codec, warning, readOnly, line, _translations, store ?? new EditableFileStore()); _dialogs.Add(dialog);
            Task done = dialog.ShowDialog(Owner); Pump(dialog); return (dialog, done);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (FileEditorDialog d in _dialogs)
                {
                    for (int i = 0; d.IsVisible && i < 60; i++)
                    {
                        foreach (Window child in d.OwnedWindows.ToArray())
                        {
                            Button? action = child.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name is "FileEditorSaveNo" or "MessageDialogAccept");
                            if (action is not null && action.IsEffectivelyVisible) { PushProcessTestDriver.Pointer(action); } else { child.Close(); }
                        }
                        d.Close(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10);
                    }
                    Assert.That(d.IsVisible || d.IsBusy, Is.False, "The editor must drain and close during fixture teardown."); d.Dispose();
                }
                Owner.Close(); Dispatcher.UIThread.RunJobs();
            }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig); Directory.Delete(Root, true); }
        }
    }
    private static async Task Ready(FileEditorDialog d)
    { await d.Initialization.WaitAsync(TimeSpan.FromSeconds(10)); await Until(() => !d.IsBusy && d.Snapshot is not null); Assert.That(d.LastError, Is.Null, d.LastException?.ToString()); Pump(d); }
    private static void Edit(FileEditorDialog d, string text) { d.Viewer.FocusEditor(); Press(d, PhysicalKey.A, RawInputModifiers.Control); d.KeyTextInput(text); Pump(d); }
    private static void SelectionThenEscape(FileEditorDialog d)
    { d.Viewer.FocusEditor(); Press(d, PhysicalKey.A, RawInputModifiers.Control); Assert.That(d.Viewer.SelectionLength, Is.GreaterThan(0)); Press(d, PhysicalKey.Escape); Assert.That(d.Viewer.SelectionLength, Is.Zero); Assert.That(d.IsVisible, Is.True); Press(d, PhysicalKey.Escape); }
    private static async Task<Window> Question(FileEditorDialog d) { await Until(() => d.OwnedWindows.Any(w => w.IsVisible && w.Name == "FileEditorSaveQuestion")); return d.OwnedWindows.Single(w => w.IsVisible && w.Name == "FileEditorSaveQuestion"); }
    private static async Task<Window> Message(FileEditorDialog d) { await Until(() => d.OwnedWindows.Any(w => w.IsVisible && w.Name == "MessageDialog")); return d.OwnedWindows.Single(w => w.IsVisible && w.Name == "MessageDialog"); }
    private static T Field<T>(TopLevel owner, string name) where T : Control => PushProcessTestDriver.Field<T>(owner, name);
    private static void Click(TopLevel owner, string name) { Pump(owner); PushProcessTestDriver.Pointer(Field<Button>(owner, name)); Pump(owner); }
    private static void Press(TopLevel owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } Pump(owner); }
    private static void Pump(TopLevel owner) { Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> complete) { Stopwatch timer = Stopwatch.StartNew(); while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(complete(), Is.True, "File editor did not settle."); }
    private static void Capture(Window owner, string name)
    { Pump(owner); using var frame = owner.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string folder = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(folder); string path = System.IO.Path.Combine(folder, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
}
