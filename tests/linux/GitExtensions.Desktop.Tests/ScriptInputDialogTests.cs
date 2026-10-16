using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
public sealed class ScriptInputDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("en", true)]
    [TestCase("fr", false)]
    [TestCase("fr", true)]
    public async Task OriginalCompactGeometryThemesAndTranslationKeepAllThreeInputsUsable(string language, bool dark)
    {
        using OwnerFixture f = new(); ThemeVariant theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        string[] values = Enumerable.Range(0, 15).Select(i => $"{i:00} literal λ " + new string('x', 110)).ToArray();
        QuickStringSelectorDialog choices = new(values, Catalog(language)) { RequestedThemeVariant = theme };
        Task modal = choices.ShowDialog(f.Owner); Pump(choices);
        ListBox list = Field<ListBox>(choices, "lbxRefs"); Border frame = Field<Border>(choices, "QuickStringsFrame");
        double itemHeight = ((ListBoxItem)list.ContainerFromIndex(0)!).Bounds.Height;
        Assert.That(list.Items.OfType<string>(), Is.EqualTo(values)); Assert.That(list.SelectedIndex, Is.Zero);
        Assert.That(choices.SystemDecorations, Is.EqualTo(SystemDecorations.None));
        Assert.That(frame.Bounds.Height, Is.EqualTo(8 * itemHeight + 2).Within(.1));
        Assert.That(Field<Grid>(choices, "flowLayoutPanel1").Bounds.Height, Is.EqualTo(29));
        Assert.That(list.GetVisualDescendants().OfType<ScrollViewer>().Single().Extent.Height, Is.GreaterThan(list.Bounds.Height));
        Assert.That(choices.FocusManager!.GetFocusedElement(), Is.SameAs(list)); Capture(choices, $"script-strings-{language}-{theme}.png");
        Press(choices, PhysicalKey.Escape); await modal; Assert.That(choices.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));

        SimplePromptDialog prompt = new("  Literal script title  ", "Literal & label", "  exact λ  ", Catalog(language)) { RequestedThemeVariant = theme };
        modal = prompt.ShowDialog(f.Owner); Pump(prompt); TextBox input = Field<TextBox>(prompt, "txtUserInput");
        Assert.That(prompt.ClientSize, Is.EqualTo(new Size(334, 76))); Assert.That(Bounds(prompt, input), Is.EqualTo(new Rect(12, 21, 310, 23)));
        Assert.That(Field<TextBlock>(prompt, "labelInput").Text, Is.EqualTo("Literal & label:"));
        Assert.That(prompt.Title, Is.EqualTo("  Literal script title  ")); Assert.That(prompt.FocusManager!.GetFocusedElement(), Is.SameAs(input));
        Assert.That(input.SelectedText, Is.EqualTo("  exact λ  ")); Assert.That(prompt.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
        Capture(prompt, $"script-simple-{language}-{theme}.png");
        prompt.Width = 534; prompt.Height = 176; Pump(prompt);
        Assert.That(Bounds(prompt, input), Is.EqualTo(new Rect(12, 21, 510, 23)));
        Assert.That(Bounds(prompt, Field<Button>(prompt, "btnOk")), Is.EqualTo(new Rect(447, 150, 75, 23)));
        Press(prompt, PhysicalKey.Escape); Assert.That(prompt.IsVisible, Is.True);
        Press(prompt, PhysicalKey.Escape); await modal;

        FilePromptDialog files = new(Catalog(language)) { RequestedThemeVariant = theme }; modal = files.ShowDialog(f.Owner); Pump(files);
        TextBox path = Field<TextBox>(files, "txtFilePath"); Button browse = Field<Button>(files, "btnBrowse"), ok = Field<Button>(files, "btnOk");
        Assert.That(files.ClientSize, Is.EqualTo(new Size(549, 78))); Assert.That(files.MinWidth, Is.EqualTo(450)); Assert.That(files.MaxWidth, Is.EqualTo(800));
        Assert.That(Bounds(files, browse).X, Is.EqualTo(Bounds(files, ok).X));
        Assert.That(browse.Bounds.Width, Is.EqualTo(115)); Assert.That(ok.Bounds.Width, Is.EqualTo(115));
        Assert.That(path.Bounds.Width, Is.GreaterThan(100));
        Assert.That(files.FocusManager!.GetFocusedElement(), Is.SameAs(path)); Assert.That(files.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(2));
        Assert.That(files.Title, Is.EqualTo(Catalog(language).Translate("FormFilePrompt", "$this", "Text", "Select script files")));
        Capture(files, $"script-files-{language}-{theme}.png");
        foreach (double width in new[] { 450d, 800d }) { files.Width = width; Pump(files); AssertInside(files, path, browse, ok); }
        Press(files, PhysicalKey.Escape); await modal; Assert.That(files.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task QuickStringUsesCultureSortDuplicatesAndRealSelectionAcceptance(int action)
    {
        using OwnerFixture f = new(); CultureInfo saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            string[] values = ["ä", "a", "z", "a", ""];
            QuickStringSelectorDialog dialog = new(values); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
            ListBox list = Field<ListBox>(dialog, "lbxRefs"); Assert.That(list.Items.OfType<string>(), Is.EqualTo(new[] { "", "a", "a", "z", "ä" }));
            Assert.That(dialog.SelectedString, Is.EqualTo("")); Assert.That(dialog.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
            if (action == 0) { dialog.KeyTextInput("z"); Pump(dialog); Assert.That(dialog.SelectedString, Is.EqualTo("z")); Press(dialog, PhysicalKey.Enter); }
            else if (action == 1) { Press(dialog, PhysicalKey.ArrowDown); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAction")); }
            else
            {
                ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(4)!; PushProcessTestDriver.Pointer(row);
                Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), dialog)!.Value;
                Assert.That((dialog.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
                dialog.MouseDown(point, MouseButton.Left); if (dialog.IsVisible) { dialog.MouseUp(point, MouseButton.Left); } Dispatcher.UIThread.RunJobs();
            }
            await modal; Assert.That(dialog.WasAccepted, Is.True); Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Accepted, action == 0 ? "z" : action == 1 ? "a" : "ä")));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [AvaloniaTest]
    public async Task QuickStringCancellationIsExplicitEvenWhenARealStringRemainsSelectedAndEmptyListsCancel()
    {
        using OwnerFixture f = new(); QuickStringSelectorDialog dialog = new(["cancelled value", "other"]); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        Press(dialog, PhysicalKey.Escape); await modal; Assert.That(dialog.SelectedString, Is.EqualTo("cancelled value"));
        Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Cancelled))); Assert.That(dialog.WasAccepted, Is.False);
        QuickStringSelectorDialog empty = new([]); modal = empty.ShowDialog(f.Owner); Pump(f.Owner); await modal;
        Assert.That(empty.SelectedString, Is.Null); Assert.That(empty.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
        QuickStringSelectorDialog blank = new([""]); modal = blank.ShowDialog(f.Owner); Pump(blank); Press(blank, PhysicalKey.Enter); await modal;
        Assert.That(blank.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Accepted, "")));
    }

    [AvaloniaTest]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("  exact default λ  ")]
    public async Task SimplePromptPreservesDefaultAndAcceptsTheEmptyString(string? value)
    {
        using OwnerFixture f = new(); SimplePromptDialog dialog = new("", "", value); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        Assert.That(dialog.Title, Is.EqualTo("User input")); Assert.That(Field<TextBlock>(dialog, "labelInput").Text, Is.EqualTo("Please specify your input:"));
        Press(dialog, PhysicalKey.Enter); await modal; Assert.That(dialog.UserInput, Is.EqualTo(value ?? "")); Assert.That(dialog.Reply.Kind, Is.EqualTo(ScriptReplyKind.Accepted));
    }

    [AvaloniaTest]
    public async Task SimplePromptEscapeFirstOnlyDeselectsAndThenCancelsWithoutReturningTheDefault()
    {
        using OwnerFixture f = new(); SimplePromptDialog dialog = new(defaultValue: "  do not leak  "); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        TextBox input = Field<TextBox>(dialog, "txtUserInput"); Assert.That(input.SelectedText, Is.EqualTo("  do not leak  "));
        Press(dialog, PhysicalKey.Escape); Assert.That(dialog.IsVisible, Is.True); Assert.That(input.SelectedText, Is.Empty); Assert.That(input.Text, Is.EqualTo("  do not leak  "));
        Press(dialog, PhysicalKey.Escape); await modal; Assert.That(dialog.UserInput, Is.Empty); Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Cancelled)));
    }

    [AvaloniaTest]
    public async Task SimplePromptEscapeOnOkDoesNothingThenReturningToInputAllowsDeselectAndCancel()
    {
        using OwnerFixture f = new(); SimplePromptDialog dialog = new(defaultValue: "  retained default  ");
        Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        TextBox input = Field<TextBox>(dialog, "txtUserInput"); Button ok = Field<Button>(dialog, "btnOk");
        Assert.That(dialog.FocusManager!.GetFocusedElement(), Is.SameAs(input));
        Press(dialog, PhysicalKey.Tab); Pump(dialog); Assert.That(dialog.FocusManager.GetFocusedElement(), Is.SameAs(ok));
        Press(dialog, PhysicalKey.Escape); Pump(dialog);
        Assert.That(dialog.IsVisible, Is.True, "Windows handles Escape only in txtUserInput, with no CancelButton.");
        Assert.That(modal.IsCompleted, Is.False); Assert.That(dialog.FocusManager.GetFocusedElement(), Is.SameAs(ok));
        Press(dialog, PhysicalKey.Tab, RawInputModifiers.Shift); Pump(dialog);
        Assert.That(dialog.FocusManager.GetFocusedElement(), Is.SameAs(input));
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); Assert.That(input.SelectedText, Is.EqualTo("  retained default  "));
        Press(dialog, PhysicalKey.Escape); Assert.That(dialog.IsVisible, Is.True); Assert.That(modal.IsCompleted, Is.False);
        Assert.That(input.SelectedText, Is.Empty); Assert.That(input.Text, Is.EqualTo("  retained default  "));
        Press(dialog, PhysicalKey.Escape); await modal;
        Assert.That(dialog.WasAccepted, Is.False); Assert.That(dialog.UserInput, Is.Empty);
        Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Cancelled)));
    }

    [AvaloniaTest]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("  \"manual file\" λ \\  ")]
    public async Task FilePromptOnlyAnEmptyStringCancelsAndNeverTrimsManualInput(string value)
    {
        using OwnerFixture f = new(); FilePromptDialog dialog = new(); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        dialog.KeyTextInput(value); Pump(dialog); Press(dialog, PhysicalKey.Enter); await modal;
        Assert.That(dialog.WasAccepted, Is.EqualTo(value.Length > 0)); Assert.That(dialog.UserInput, Is.EqualTo(value));
        Assert.That(dialog.Reply, Is.EqualTo(value.Length == 0 ? new ScriptReply(ScriptReplyKind.Cancelled) : new ScriptReply(ScriptReplyKind.Accepted, value)));
    }

    [AvaloniaTest]
    public async Task FileBrowseReplacesManualInputWithQuotedExistingLiteralFilesAndCancelKeepsIt()
    {
        using OwnerFixture f = new(); string[] paths = [f.File("a \"quoted\"\tλ.txt"), f.File("b\nline.txt")]; int calls = 0;
        FilePromptDialog dialog = new(browseFiles: (initial, _) =>
        { Assert.That(initial, Is.EqualTo(".")); return Task.FromResult<IReadOnlyList<string>>(++calls == 1 ? paths : []); });
        Task modal = dialog.ShowDialog(f.Owner); Pump(dialog); dialog.KeyTextInput("manual input");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse")); await dialog.BrowseCompletion; Pump(dialog);
        string expected = '"' + paths[0].Replace("\"", "\\\"", StringComparison.Ordinal) + "\" \"" + paths[1] + '"';
        Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo(expected));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse")); await dialog.BrowseCompletion;
        Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo(expected));
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnOk")); await modal; Assert.That(dialog.Reply.Value, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ClosingFilePromptCancelsAndDrainsPendingBrowseAndIgnoresLateResults(bool closeOwner)
    {
        using OwnerFixture f = new(); TaskCompletionSource<IReadOnlyList<string>> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        FilePromptDialog dialog = new(browseFiles: async (_, token) =>
        { try { return await response.Task.WaitAsync(token); } catch (OperationCanceledException) { cancelled = true; throw; } });
        Task modal = dialog.ShowDialog(f.Owner); Pump(dialog); dialog.KeyTextInput("untouched");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse")); Assert.That(dialog.IsBrowsing, Is.True);
        if (closeOwner) { f.Owner.Close(); } else { Press(dialog, PhysicalKey.Escape); }
        await modal; await dialog.BrowseCompletion; Assert.That(cancelled, Is.True); Assert.That(dialog.IsBrowsing, Is.False);
        response.SetResult([f.File("too late.txt")]); Pump(f.Owner);
        Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo("untouched")); Assert.That(dialog.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
    }

    [AvaloniaTest]
    public async Task APickerThatCompletesLateWithoutObservingCancellationCannotReplaceClosedInput()
    {
        using OwnerFixture f = new(); TaskCompletionSource<IReadOnlyList<string>> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FilePromptDialog dialog = new(browseFiles: (_, _) => response.Task); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
        dialog.KeyTextInput("preserve literal"); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse"));
        Press(dialog, PhysicalKey.Escape); await modal; Assert.That(dialog.BrowseCompletion.IsCompleted, Is.False);
        response.SetResult([f.File("late native result.txt")]); await dialog.BrowseCompletion;
        Assert.That(dialog.LastBrowseError, Is.Null); Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo("preserve literal"));
        Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Cancelled)));
    }

    [AvaloniaTest]
    public async Task ClosingDuringThePickerCallDoesNotTurnCancellationIntoAnObjectDisposedError()
    {
        using OwnerFixture f = new(); FilePromptDialog? dialog = null;
        dialog = new(browseFiles: (_, _) =>
        { dialog!.Close(); return Task.FromResult<IReadOnlyList<string>>([f.File("closed.txt")]); });
        Task modal = dialog.ShowDialog(f.Owner); Pump(dialog); dialog.KeyTextInput("unchanged");
        PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse")); await modal; await dialog.BrowseCompletion;
        Assert.That(dialog.LastBrowseError, Is.Null); Assert.That(dialog.Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
        Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo("unchanged"));
    }

    [AvaloniaTest]
    public async Task InvalidBrowseFilesShowAnErrorAndLeaveThePromptAndLiteralInputIntact()
    {
        using OwnerFixture f = new(); FilePromptDialog dialog = new(browseFiles: (_, _) => Task.FromResult<IReadOnlyList<string>>([Path.Combine(f.Directory, "missing.txt")]));
        Task modal = dialog.ShowDialog(f.Owner); Pump(dialog); dialog.KeyTextInput(" literal "); PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnBrowse"));
        await Until(() => dialog.OwnedWindows.Count > 0); Window error = dialog.OwnedWindows.Single(); Pump(error);
        PushProcessTestDriver.Pointer(error.GetVisualDescendants().OfType<Button>().Single()); await dialog.BrowseCompletion;
        Assert.That(dialog.LastBrowseError, Is.TypeOf<IOException>()); Assert.That(Field<TextBox>(dialog, "txtFilePath").Text, Is.EqualTo(" literal "));
        Press(dialog, PhysicalKey.Escape); await modal;
    }

    [AvaloniaTest]
    public async Task ExplicitCancelledReplyCanAbortTheExistingTemplateWithoutAnyScriptLaunch()
    {
        using OwnerFixture f = new(); ScriptExpansionResult result = await ScriptTemplate.PrepareAsync(new ScriptDefinition { Command = "fixture-never-launched", Arguments = "{UserInput:name=untouched}" }, new ScriptContext(), async (request, _) =>
        {
            Assert.That(request.Kind, Is.EqualTo(ScriptRequestKind.UserInput));
            SimplePromptDialog dialog = new("Script", request.Label, request.DefaultValue); Task modal = dialog.ShowDialog(f.Owner); Pump(dialog);
            Press(dialog, PhysicalKey.Escape); Press(dialog, PhysicalKey.Escape); await modal; return dialog.Reply;
        });
        Assert.That(result.Status, Is.EqualTo(ScriptExpansionStatus.Cancelled)); Assert.That(result.Arguments, Is.Null);
    }

    private static TranslationCatalog Catalog(string language) => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(Window dialog, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { WindowBase? owner = dialog.Owner; dialog.KeyPressQwerty(key, modifiers); if (dialog.IsVisible) { dialog.KeyReleaseQwerty(key, modifiers); } else if (owner?.IsVisible == true) { owner.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static void Pump(TopLevel root)
    { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static Rect Bounds(Window root, Control control) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);
    private static void AssertInside(Window root, params Control[] controls)
    { foreach (Control control in controls) { Rect bounds = Bounds(root, control); Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Right, Is.LessThanOrEqualTo(root.ClientSize.Width)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(root.ClientSize.Height)); } }
    private static async Task Until(Func<bool> ready)
    { Stopwatch watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static void Capture(TopLevel root, string name)
    { Pump(root); using var frame = root.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; System.IO.Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class OwnerFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "script-input-tests-" + Guid.NewGuid().ToString("N"));
        internal Window Owner { get; } = new() { Width = 1000, Height = 700 };
        internal OwnerFixture() { System.IO.Directory.CreateDirectory(Directory); Owner.Show(); Pump(Owner); }
        internal string File(string name) { string path = Path.Combine(Directory, name); System.IO.File.WriteAllText(path, "fixture\n"); return path; }
        public void Dispose() { Owner.Close(); if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); } }
    }
}
