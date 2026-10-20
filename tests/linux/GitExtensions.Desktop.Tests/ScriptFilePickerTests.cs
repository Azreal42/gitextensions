using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Dialogs;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptFilePickerTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task DefaultBrowseOpensARealOwnedMultiplePickerAndEscapeKeepsTheLiteral(string language, bool dark)
    {
        using Fixture f = new();
        FilePromptDialog prompt = new(TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language)))
            { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Task modal = prompt.ShowDialog(f.Owner); Pump(prompt); prompt.KeyTextInput("  exact manual λ  ");
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "btnBrowse"));
        ScriptFilePickerDialog picker = await Picker(prompt); Pump(picker);
        Assert.That(picker.Owner, Is.SameAs(prompt)); Assert.That(picker.ShowInTaskbar, Is.False);
        Assert.That(picker.ActualThemeVariant, Is.EqualTo(prompt.ActualThemeVariant));
        Assert.That(picker.Title, Is.EqualTo(prompt.Title));
        Assert.That(Field<Button>(picker, "ScriptFileOpen").Content, Is.EqualTo(language == "fr" ? "Ouvrir" : "Open"));
        Assert.That(Field<TextBox>(picker, "ScriptFileName").Watermark, Is.EqualTo(language == "fr" ? "Nom du fichier" : "File name"));
        Assert.That(Field<ListBox>(picker, "PART_Files").SelectionMode, Is.EqualTo(SelectionMode.Multiple));
        Assert.That(Field<TextBox>(picker, "Location").Text, Is.EqualTo(Path.GetFullPath(".")));
        Assert.That(picker.GetVisualDescendants().OfType<ManagedFileChooser>().Count(), Is.EqualTo(1));
        Capture(picker, $"script-file-picker-{language}.png");
        Press(picker, PhysicalKey.Escape); await prompt.BrowseCompletion;
        Assert.That(picker.IsVisible, Is.False); Assert.That(prompt.OwnedWindows, Is.Empty);
        Assert.That(prompt.IsBrowsing, Is.False); Assert.That(prompt.LastBrowseError, Is.Null);
        Assert.That(Field<TextBox>(prompt, "txtFilePath").Text, Is.EqualTo("  exact manual λ  "));
        Assert.That(prompt.FocusManager!.GetFocusedElement(), Is.SameAs(Field<TextBox>(prompt, "txtFilePath")));
        Press(prompt, PhysicalKey.Enter); await modal;
        Assert.That(prompt.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Accepted, "  exact manual λ  ")));
    }

    [AvaloniaTest]
    public async Task LocationNavigationAndRealMultipleSelectionReturnLiteralPathsThroughTheDefaultDispatcher()
    {
        using Fixture f = new(); string[] paths = [f.File("a quote\" λ.txt"), f.File("b\tline\nfile.txt")];
        ScriptPreparationDispatcher dispatcher = new(f.Owner, new ScriptContext(), new([], []));
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script());
        await Until(() => f.Owner.OwnedWindows.OfType<FilePromptDialog>().Any());
        FilePromptDialog prompt = f.Owner.OwnedWindows.OfType<FilePromptDialog>().Single();
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "btnBrowse"));
        ScriptFilePickerDialog picker = await Picker(prompt); Navigate(picker, f.Directory);
        ListBox list = Field<ListBox>(picker, "PART_Files"); Assert.That(list.ItemCount, Is.EqualTo(2));
        ListBoxItem first = (ListBoxItem)list.ContainerFromIndex(0)!;
        PushProcessTestDriver.Pointer(first);
        Press(picker, PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(list.SelectedItems, Has.Count.EqualTo(2));
        Button accept = Finalize(picker).First(); Assert.That(accept.IsEffectivelyEnabled, Is.True);
        PushProcessTestDriver.Pointer(accept); await prompt.BrowseCompletion;
        string expected = string.Join(' ', paths.Select(ScriptInputDialogControls.QuoteFilePath));
        Assert.That(Field<TextBox>(prompt, "txtFilePath").Text, Is.EqualTo(expected));
        Press(prompt, PhysicalKey.Enter); await Until(() => preparing.IsCompleted);
        ScriptPreparationResult result = await preparing;
        Assert.That(result.Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Completed));
        Assert.That(result.Expansion.Arguments, Is.EqualTo(expected));
        Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task DefaultPickerReallyClosesAndPreparationDrainsOnTokenPromptOrOwnerCancellation(int action)
    {
        using Fixture f = new(); using CancellationTokenSource cancellation = new();
        ScriptPreparationDispatcher dispatcher = new(f.Owner, new ScriptContext(), new([], []));
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script(), cancellation.Token);
        await Until(() => f.Owner.OwnedWindows.OfType<FilePromptDialog>().Any());
        FilePromptDialog prompt = f.Owner.OwnedWindows.OfType<FilePromptDialog>().Single();
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "btnBrowse")); ScriptFilePickerDialog picker = await Picker(prompt);
        Assert.That(prompt.BrowseCompletion.IsCompleted, Is.False);
        if (action == 0) { await Task.Run(cancellation.Cancel); }
        else if (action == 1) { prompt.Close(); }
        else { f.Owner.Close(); }
        await Until(() => preparing.IsCompleted);
        if (action == 1) { Assert.That((await preparing).Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Cancelled)); }
        else { Assert.ThrowsAsync<OperationCanceledException>(async () => await preparing); }
        await prompt.BrowseCompletion;
        Assert.That(picker.IsVisible, Is.False); Assert.That(prompt.IsVisible, Is.False);
        Assert.That(prompt.IsBrowsing, Is.False); Assert.That(prompt.LastBrowseError, Is.Null);
        Assert.That(prompt.WasAccepted, Is.False); Assert.That(prompt.UserInput, Is.Empty);
        Assert.That(f.Owner.OwnedWindows, Is.Empty); Assert.That(prompt.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task PickerCancelButtonPreservesInputAndAnotherBrowseCanAcceptAFile()
    {
        using Fixture f = new(); string file = f.File("existing λ.txt");
        FilePromptDialog prompt = new(); Task modal = prompt.ShowDialog(f.Owner); Pump(prompt); prompt.KeyTextInput("preserved");
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "btnBrowse")); ScriptFilePickerDialog picker = await Picker(prompt);
        PushProcessTestDriver.Pointer(Finalize(picker).Last()); await prompt.BrowseCompletion;
        Assert.That(Field<TextBox>(prompt, "txtFilePath").Text, Is.EqualTo("preserved"));
        PushProcessTestDriver.Pointer(Field<Button>(prompt, "btnBrowse")); picker = await Picker(prompt); Navigate(picker, f.Directory);
        ListBox list = Field<ListBox>(picker, "PART_Files"); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!);
        PushProcessTestDriver.Pointer(Finalize(picker).First()); await prompt.BrowseCompletion;
        Assert.That(Field<TextBox>(prompt, "txtFilePath").Text, Is.EqualTo(ScriptInputDialogControls.QuoteFilePath(file)));
        Press(prompt, PhysicalKey.Enter); await modal; Assert.That(prompt.Reply.Value, Is.EqualTo(ScriptInputDialogControls.QuoteFilePath(file)));
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task TypedFileNameReplacesAnOlderSelectionAndReturnsExistingLiteralPaths(int form)
    {
        using Fixture f = new(); string old = f.File("a old.txt");
        string literal = f.File("b quote\" space\tλ.txt"), one = f.File("c one λ.txt"), two = f.File("d two.txt");
        Task<IReadOnlyList<string>> choosing = ScriptFilePickerDialog.PickAsync(f.Owner, "Select files", f.Directory, default);
        ScriptFilePickerDialog picker = f.Owner.OwnedWindows.OfType<ScriptFilePickerDialog>().Single(); Pump(picker);
        ListBox list = Field<ListBox>(picker, "PART_Files"); PushProcessTestDriver.Pointer((ListBoxItem)list.ContainerFromIndex(0)!);
        TextBox name = Field<TextBox>(picker, "ScriptFileName"); PushProcessTestDriver.Pointer(name); Press(picker, PhysicalKey.A, RawInputModifiers.Control);
        string text = form == 0 ? Path.GetFileName(literal) : form == 1 ? literal : "\"" + Path.GetFileName(one) + "\" \"" + Path.GetFileName(two) + "\"";
        picker.KeyTextInput(text); Pump(picker);
        Assert.That(Field<Button>(picker, "ScriptFileOpen").IsEffectivelyEnabled, Is.True);
        if (form == 1) { Press(picker, PhysicalKey.Enter); } else { PushProcessTestDriver.Pointer(Field<Button>(picker, "ScriptFileOpen")); }
        await Until(() => choosing.IsCompleted);
        Assert.That(await choosing, Is.EqualTo(form == 2 ? new[] { one, two } : new[] { literal }));
        Assert.That(await choosing, Does.Not.Contain(old)); Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task InvalidTypedNameCannotAcceptAnOldSelectionAndDirectoryInputNavigatesBeforeRetry()
    {
        using Fixture f = new(); string file = f.File("a existing.txt");
        string folder = Path.Combine(f.Directory, "child λ"); System.IO.Directory.CreateDirectory(folder);
        string nested = Path.Combine(folder, "nested.txt"); System.IO.File.WriteAllText(nested, "nested");
        Task<IReadOnlyList<string>> choosing = ScriptFilePickerDialog.PickAsync(f.Owner, "Select files", f.Directory, default);
        ScriptFilePickerDialog picker = f.Owner.OwnedWindows.OfType<ScriptFilePickerDialog>().Single(); Pump(picker);
        ListBox list = Field<ListBox>(picker, "PART_Files");
        ListBoxItem item = (ListBoxItem)list.ContainerFromIndex(1)!; PushProcessTestDriver.Pointer(item);
        TextBox name = Field<TextBox>(picker, "ScriptFileName");
        PushProcessTestDriver.Pointer(name); Press(picker, PhysicalKey.A, RawInputModifiers.Control); picker.KeyTextInput("missing.txt");
        PushProcessTestDriver.Pointer(Field<Button>(picker, "ScriptFileOpen"));
        await Until(() => picker.OwnedWindows.Count == 1);
        Assert.That(choosing.IsCompleted, Is.False); Assert.That(picker.IsVisible, Is.True);
        Window error = picker.OwnedWindows.Single(); Pump(error); PushProcessTestDriver.Pointer(error.GetVisualDescendants().OfType<Button>().Single());
        await Until(() => Field<Button>(picker, "ScriptFileOpen").IsEffectivelyEnabled);
        PushProcessTestDriver.Pointer(name); Press(picker, PhysicalKey.A, RawInputModifiers.Control); picker.KeyTextInput(folder);
        PushProcessTestDriver.Pointer(Field<Button>(picker, "ScriptFileOpen")); Pump(picker);
        Assert.That(Field<TextBox>(picker, "Location").Text, Is.EqualTo(folder)); Assert.That(choosing.IsCompleted, Is.False);
        PushProcessTestDriver.Pointer(name); picker.KeyTextInput("nested.txt"); Press(picker, PhysicalKey.Enter);
        await Until(() => choosing.IsCompleted); Assert.That(await choosing, Is.EqualTo(new[] { nested }));
        Assert.That(System.IO.File.ReadAllText(file), Is.EqualTo("fixture"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationWithAnOwnedValidationErrorClosesAndDrainsBothChildren(bool ownerClose)
    {
        using Fixture f = new(); using CancellationTokenSource cancellation = new();
        Task<IReadOnlyList<string>> choosing = ScriptFilePickerDialog.PickAsync(f.Owner, "Select files", f.Directory, cancellation.Token);
        ScriptFilePickerDialog picker = f.Owner.OwnedWindows.OfType<ScriptFilePickerDialog>().Single(); Pump(picker);
        TextBox name = Field<TextBox>(picker, "ScriptFileName"); PushProcessTestDriver.Pointer(name); picker.KeyTextInput("missing.txt");
        PushProcessTestDriver.Pointer(Field<Button>(picker, "ScriptFileOpen"));
        await Until(() => picker.OwnedWindows.Count == 1); Window error = picker.OwnedWindows.Single();
        Assert.That(choosing.IsCompleted, Is.False);
        if (ownerClose) { f.Owner.Close(); } else { await Task.Run(cancellation.Cancel); }
        await Until(() => choosing.IsCompleted);
        if (ownerClose) { Assert.That(await choosing, Is.Empty); }
        else { Assert.ThrowsAsync<OperationCanceledException>(async () => await choosing); }
        Assert.That(error.IsVisible, Is.False); Assert.That(picker.IsVisible, Is.False);
        Assert.That(picker.OwnedWindows, Is.Empty); Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task CancellationBeforeOpeningCreatesNoPickerWindow()
    {
        using Fixture f = new(); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ScriptFilePickerDialog.PickAsync(f.Owner, "Select files", f.Directory, cancellation.Token));
        Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    private static ScriptDefinition Script() => new() { Command = "never-launched", Arguments = "{UserFiles}" };
    private static Button[] Finalize(Window picker) => Field<Avalonia.Controls.Primitives.UniformGrid>(picker, "Finalize").Children.OfType<Button>().ToArray();
    private static async Task<ScriptFilePickerDialog> Picker(FilePromptDialog prompt)
    { await Until(() => prompt.LastBrowseError is not null || prompt.OwnedWindows.OfType<ScriptFilePickerDialog>().Any());
        Assert.That(prompt.LastBrowseError, Is.Null, prompt.LastBrowseError?.ToString());
        ScriptFilePickerDialog picker = prompt.OwnedWindows.OfType<ScriptFilePickerDialog>().Single(); Pump(picker); return picker; }
    private static void Navigate(Window picker, string directory)
    { TextBox location = Field<TextBox>(picker, "Location"); PushProcessTestDriver.Pointer(location); Press(picker, PhysicalKey.A, RawInputModifiers.Control); picker.KeyTextInput(directory); Press(picker, PhysicalKey.Enter); Pump(picker); }
    private static T Field<T>(TopLevel root, string name) where T : Control
    {
        Control control = name switch
        {
            "ScriptFileName" => root.GetVisualDescendants().OfType<ManagedFileChooser>().Single().GetVisualDescendants().OfType<TextBox>().Single(box => box.Name != "Location"),
            "ScriptFileOpen" => Finalize((Window)root).First(),
            _ => PushProcessTestDriver.Field<T>(root, name)
        };
        return (T)control;
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(condition(), Is.True); }
    private static void Capture(Window window, string name)
    { Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; System.IO.Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "script-picker-" + Guid.NewGuid().ToString("N"));
        internal Window Owner { get; } = new() { Width = 1000, Height = 700 };
        internal Fixture() { System.IO.Directory.CreateDirectory(Directory); Owner.Show(); Pump(Owner); }
        internal string File(string name) { string path = Path.Combine(Directory, name); System.IO.File.WriteAllText(path, "fixture"); return path; }
        public void Dispose() { Owner.Close(); if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }
}
