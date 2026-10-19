using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BugReportDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalVisibleTabsFieldsAndExplicitCopyStayLocalAndPreserveTheDescription(string language, bool dark)
    {
        List<string> copied = []; List<Uri> opened = [];
        await using BugReportUiScope f = new(language, dark, copy: (text, _) => { copied.Add(text); return Task.CompletedTask; },
            open: (uri, _) => { opened.Add(uri); return Task.CompletedTask; });
        BugReportDialog report = f.Dialog;
        Assert.That(report.ClientSize, Is.EqualTo(new Size(558, 469)));
        Assert.That(report.CanMinimize, Is.False); Assert.That(report.Topmost, Is.True);
        Assert.That(f.Field<TabControl>("mainTabs").Items.OfType<TabItem>().Select(tab => tab.Name), Is.EqualTo(new[] { "generalTabPage", "exceptionTabPage" }));
        Assert.That(report.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "reportContentsTabPage"), Is.False);
        Assert.That(f.Field<TextBox>("exceptionTextBox").Text, Is.EqualTo(f.Snapshot.Root.Type));
        Assert.That(f.Field<TextBox>("exceptionMessageTextBox").Text, Is.EqualTo(f.Snapshot.Root.Message));
        foreach (string name in new[] { "exceptionTextBox", "exceptionMessageTextBox", "targetSiteTextBox", "applicationTextBox", "gitTextBox", "dateTimeTextBox", "clrTextBox" })
        { Assert.That(f.Field<TextBox>(name).IsReadOnly, Is.True, name); }
        Assert.That(f.Field<TextBox>("descriptionTextBox").IsKeyboardFocusWithin, Is.True);
        Assert.That(copied, Is.Empty); Assert.That(opened, Is.Empty);
        f.Edit("private/path Ω\nreproduction details");
        PortableHelperSettingsFixture.Press(report, PhysicalKey.Enter);
        Assert.That(f.Completion.IsCompleted, Is.False); Assert.That(f.Field<TextBox>("descriptionTextBox").Text, Does.EndWith("\n"));
        PortableHelperSettingsFixture.Press(report, PhysicalKey.Tab);
        Assert.That(f.Field<Button>("btnCopy").IsKeyboardFocusWithin, Is.True);
        PortableHelperSettingsFixture.Press(report, PhysicalKey.Enter);
        await PushProcessTestDriver.UntilAsync(report, () => !report.IsBusy);
        Assert.That(copied, Is.EqualTo(new[] { f.Context.CopyText(f.Snapshot, f.Field<TextBox>("descriptionTextBox").Text) }));
        Assert.That(opened, Is.Empty); Assert.That(report.IsVisible, Is.True);
        Assert.That(f.Field<Button>("btnCopy").IsKeyboardFocusWithin, Is.True);
        foreach (Button button in new[] { f.Field<Button>("btnCopy"), f.Field<Button>("sendAndQuitButton"), f.Field<Button>("quitButton") })
        {
            Point origin = button.TranslatePoint(default, report)!.Value;
            Assert.That(new Rect(default, report.ClientSize).Contains(new Rect(origin, button.Bounds.Size)), Is.True, button.Name);
            foreach (AccessText label in button.GetVisualDescendants().OfType<AccessText>())
            {
                string visible = f.Host.Original("BugReportForm", button.Name + ".Text").Replace("&", "", StringComparison.Ordinal);
                FormattedText glyphs = new(visible, f.Host.Translations.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black);
                double left = label.TranslatePoint(default, button)!.Value.X;
                Assert.That(left + glyphs.Width, Is.LessThanOrEqualTo(button.Bounds.Width - button.Padding.Right + .5), "The translated glyphs must fit, not just their clipped presenter: " + button.Name);
            }
        }
        Assert.That(f.Label("btnCopy"), Is.EqualTo(f.Host.Original("BugReportForm", "btnCopy.Text").Replace("&", "_", StringComparison.Ordinal)));
        PortableHelperSettingsFixture.Capture(report, "bug-report-" + language + (dark ? "-dark.png" : "-light.png"));
        PushProcessTestDriver.Pointer(f.Field<TabItem>("exceptionTabPage"));
        PortableHelperSettingsFixture.Capture(report, "bug-report-details-" + language + (dark ? "-dark.png" : "-light.png"));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SendingRequiresADescriptionAndAnExplicitReviewedConfirmationWithNoAsDefault(bool approve)
    {
        List<Uri> opened = [];
        await using BugReportUiScope f = new(open: (uri, _) => { opened.Add(uri); return Task.CompletedTask; });
        f.Edit(" \n\t "); f.Click("sendAndQuitButton");
        Window missing = await f.Child("MessageDialog");
        Assert.That(opened, Is.Empty); Assert.That(f.Completion.IsCompleted, Is.False);
        PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<Button>(missing, "MessageDialogAccept"));
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
        f.Edit("reproduction Ω with private/path"); f.Click("sendAndQuitButton");
        Window confirmation = await f.Child("ConfirmExternalErrorReport");
        Button no = PushProcessTestDriver.Field<Button>(confirmation, "CancelErrorReportSubmission");
        Assert.That(no.IsDefault && no.IsCancel && no.IsKeyboardFocusWithin, Is.True);
        Assert.That(PushProcessTestDriver.Field<TextBlock>(confirmation, "ErrorReportDestination").Text, Is.EqualTo(DesktopErrorReporter.IssueTarget.AbsoluteUri));
        Assert.That(PushProcessTestDriver.Field<TextBox>(confirmation, "ErrorReportPreview").Text, Is.EqualTo(f.Context.CopyText(f.Snapshot, "reproduction Ω with private/path")));
        Assert.That(opened, Is.Empty);
        if (approve) { PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<Button>(confirmation, "ConfirmErrorReportSubmission")); }
        else { PortableHelperSettingsFixture.Press(confirmation, PhysicalKey.Enter); }
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
        if (approve)
        {
            Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(BugReportDecision.Quit));
            Assert.That(opened.Single().AbsolutePath, Is.EqualTo("/Azreal42/gitextensions/issues/new"));
            Assert.That(Uri.UnescapeDataString(opened.Single().Query), Does.Contain("reproduction Ω with private/path"));
        }
        else
        {
            Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Completion.IsCompleted, Is.False); Assert.That(opened, Is.Empty);
            Assert.That(f.Field<TextBox>("descriptionTextBox").Text, Is.EqualTo("reproduction Ω with private/path"));
        }
        Assert.That(f.Host.Owner.IsVisible, Is.True, "The view returns a decision; it never exits the process itself.");
    }

    [AvaloniaTest]
    public async Task UserExternalDetailsFocusDisablesSubmissionAndIgnoreReturnsItsDistinctDecision()
    {
        List<Uri> opened = [];
        await using BugReportUiScope f = new(context: BugReportUiScope.StandardContext with { FocusDetails = true, ShowIgnore = true }, open: (uri, _) => { opened.Add(uri); return Task.CompletedTask; });
        Assert.That(f.Field<TabControl>("mainTabs").SelectedIndex, Is.EqualTo(1));
        Assert.That(f.Field<TreeView>("exceptionTreeView").IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Field<Button>("sendAndQuitButton").IsEnabled, Is.False);
        f.Click("IgnoreButton"); Assert.That(await f.Completion.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(BugReportDecision.Ignore));
        Assert.That(opened, Is.Empty);
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task EveryAggregateBranchShowsItsOwnPropertiesAndOpensAnOwnedReadonlyProperty(bool doubleClick)
    {
        Exception first = new("first", new Exception("first child"));
        Exception second = new("second", new Exception("second child"));
        await using BugReportUiScope f = new(snapshot: ExceptionReportSnapshot.Capture(new AggregateException(first, second)),
            context: BugReportUiScope.StandardContext with { FocusDetails = true });
        TreeView tree = f.Field<TreeView>("exceptionTreeView"); TreeViewItem root = tree.Items.OfType<TreeViewItem>().Single();
        Assert.That(root.IsExpanded, Is.True);
        TreeViewItem secondItem = root.Items.OfType<TreeViewItem>().ElementAt(1);
        secondItem.BringIntoView(); PortableHelperSettingsFixture.Pump(f.Dialog); PushProcessTestDriver.Pointer((Control)secondItem.Header!);
        Assert.That(((ExceptionReportNode)secondItem.Tag!).Message, Is.EqualTo("second"));
        Assert.That(secondItem.Items.OfType<TreeViewItem>().Single().Tag, Is.SameAs(f.Snapshot.Root.Children[1].Children.Single()));
        Assert.That(tree.IsKeyboardFocusWithin, Is.True);
        ListBox list = f.Field<ListBox>("exceptionDetailsListView");
        Assert.That(list.Items.OfType<ExceptionReportProperty>().Single(property => property.Name == "Message").Value, Is.EqualTo("second"));
        PortableHelperSettingsFixture.Press(f.Dialog, PhysicalKey.ArrowDown);
        Assert.That(((ExceptionReportNode)((TreeViewItem)tree.SelectedItem!).Tag!).Message, Is.EqualTo("second child"));
        list.SelectedIndex = 1; PortableHelperSettingsFixture.Pump(f.Dialog);
        ListBoxItem row = (ListBoxItem)list.ContainerFromIndex(1)!; row.BringIntoView(); PortableHelperSettingsFixture.Pump(f.Dialog);
        PushProcessTestDriver.Pointer(row);
        if (doubleClick) { PushProcessTestDriver.Pointer(row); }
        else { PortableHelperSettingsFixture.Press(f.Dialog, PhysicalKey.Enter); }
        Window detail = await f.Child("ExceptionDetailView");
        Assert.That(detail.Owner, Is.SameAs(f.Dialog)); Assert.That(detail.ActualThemeVariant, Is.EqualTo(f.Dialog.ActualThemeVariant));
        Assert.That(PushProcessTestDriver.Field<TextBox>(detail, "propertyTextBox").Text, Is.EqualTo("Message"));
        Assert.That(PushProcessTestDriver.Field<TextBox>(detail, "propertyInformationTextBox").Text, Is.EqualTo("second child"));
        detail.Close(); await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
        Assert.That(list.IsKeyboardFocusWithin, Is.True); Assert.That(list.SelectedIndex, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ACloseRequestWaitsForAnAlreadyStartedClipboardActionEvenIfItsCallbackIgnoresCancellation()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using BugReportUiScope f = new(copy: async (_, _) => { started.SetResult(); await release.Task; });
        f.Click("btnCopy"); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            f.Dialog.Close(); PortableHelperSettingsFixture.Pump(f.Dialog);
            Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Completion.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); }
        await PushProcessTestDriver.CompleteAsync(f.Completion, f.Host.Owner);
        Assert.That(f.Dialog.IsVisible, Is.False); Assert.That(f.Dialog.Completion.IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task AnExternalLauncherFailureKeepsTheReportAndDescriptionWithAFixedLocalNotice()
    {
        await using BugReportUiScope f = new(open: (_, _) => Task.FromException(new IOException("private launcher payload must not become the notice")));
        f.Edit("reproduce this"); f.Click("sendAndQuitButton"); Window confirmation = await f.Child("ConfirmExternalErrorReport");
        PushProcessTestDriver.Pointer(PushProcessTestDriver.Field<Button>(confirmation, "ConfirmErrorReportSubmission"));
        await PushProcessTestDriver.UntilAsync(f.Dialog, () => !f.Dialog.IsBusy);
        Assert.That(f.Dialog.IsVisible, Is.True); Assert.That(f.Field<TextBox>("descriptionTextBox").Text, Is.EqualTo("reproduce this"));
        Assert.That(f.Field<TextBlock>("BugReportNotice").Text, Is.EqualTo("Unable to complete this action."));
        Assert.That(f.Completion.IsCompleted, Is.False);
    }
    [AvaloniaTest]
    public async Task ClosingTheReportDuringSubmissionConfirmationDrainsItWithoutOpeningAnyUri()
    {
        List<Uri> opened = [];
        await using BugReportUiScope f = new(open: (uri, _) => { opened.Add(uri); return Task.CompletedTask; });
        f.Edit("details to review"); f.Click("sendAndQuitButton");
        Window confirmation = await f.Child("ConfirmExternalErrorReport");
        f.Dialog.BeginClose(); f.Dialog.Close();
        await PushProcessTestDriver.CompleteAsync(f.Completion, f.Host.Owner);
        Assert.That(confirmation.IsVisible || f.Dialog.IsVisible, Is.False); Assert.That(opened, Is.Empty);
        Assert.That(await f.Completion, Is.EqualTo(BugReportDecision.Dismiss), "An implicit close must never act like Send or Quit.");
        Assert.That(f.Dialog.Completion.IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task PresenterCancellationClosesAndDrainsTheOwnedPropertyWindow()
    {
        using PortableHelperSettingsFixture host = new(); using CancellationTokenSource cancellation = new();
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(new Exception("original"));
        Task<BugReportDecision> result = DesktopErrorReporter.ShowAsync(host.Owner, snapshot,
            BugReportUiScope.StandardContext with { FocusDetails = true }, host.Translations, cancellation.Token);
        await PushProcessTestDriver.UntilAsync(host.Owner, () => host.Owner.OwnedWindows.OfType<BugReportDialog>().Any());
        BugReportDialog report = host.Owner.OwnedWindows.OfType<BugReportDialog>().Single();
        ListBox rows = PushProcessTestDriver.Field<ListBox>(report, "exceptionDetailsListView");
        rows.SelectedIndex = 0; rows.Focus(); PortableHelperSettingsFixture.Press(report, PhysicalKey.Enter);
        await PushProcessTestDriver.UntilAsync(host.Owner, () => report.OwnedWindows.OfType<ExceptionDetailDialog>().Any());
        ExceptionDetailDialog detail = report.OwnedWindows.OfType<ExceptionDetailDialog>().Single();
        cancellation.Cancel(); await PushProcessTestDriver.CompleteAsync(result, host.Owner);
        Assert.That(await result, Is.EqualTo(BugReportDecision.Dismiss));
        Assert.That(report.IsVisible || detail.IsVisible, Is.False); Assert.That(report.Completion.IsCompletedSuccessfully, Is.True);
    }

    [AvaloniaTest]
    public async Task EnterWithoutASelectedPropertyDoesNotOpenAnEmptyDetailOrFailTheReport()
    {
        await using BugReportUiScope f = new(context: BugReportUiScope.StandardContext with { FocusDetails = true });
        ListBox rows = f.Field<ListBox>("exceptionDetailsListView"); rows.SelectedIndex = -1; rows.Focus();
        PortableHelperSettingsFixture.Press(f.Dialog, PhysicalKey.Enter);
        Assert.That(f.Dialog.OwnedWindows, Is.Empty); Assert.That(f.Dialog.IsBusy, Is.False);
        Assert.That(f.Field<TextBlock>("BugReportNotice").Text, Is.Null.Or.Empty);
        Assert.That(f.Dialog.Snapshot, Is.SameAs(f.Snapshot));
    }

}

internal sealed class BugReportUiScope : IAsyncDisposable
{
    internal static ExceptionReportContext StandardContext => new("operation", "/private/repository Ω", "7.2.1-linux-alpha+fixture",
        DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Linux fixture", ".NET fixture", "X64");
    internal PortableHelperSettingsFixture Host { get; }
    internal BugReportDialog Dialog { get; }
    internal ExceptionReportSnapshot Snapshot { get; }
    internal ExceptionReportContext Context { get; }
    internal Task<BugReportDecision> Completion { get; }
    internal BugReportUiScope(string language = "en", bool dark = false, ExceptionReportSnapshot? snapshot = null, ExceptionReportContext? context = null,
        Func<string, CancellationToken, Task>? copy = null, Func<Uri, CancellationToken, Task>? open = null)
    {
        Host = new(language, dark); Snapshot = snapshot ?? ExceptionReportSnapshot.Capture(new InvalidOperationException("local failure Ω", new ArgumentException("inner failure")));
        Context = context ?? StandardContext;
        Dialog = new(Snapshot, Context, Host.Translations, copy ?? ((_, _) => Task.CompletedTask), open) { RequestedThemeVariant = Host.Theme };
        Host.Track(Dialog); Completion = Dialog.ShowDialog<BugReportDecision>(Host.Owner); PortableHelperSettingsFixture.Pump(Dialog);
    }
    internal T Field<T>(string name) where T : Control => PushProcessTestDriver.Field<T>(Dialog, name);
    internal void Click(string name) => PushProcessTestDriver.Pointer(Field<Button>(name));
    internal string Label(string name) => Field<Button>(name).GetVisualDescendants().OfType<AccessText>().Single().Text ?? "";
    internal void Edit(string description)
    { Host.Edit(Dialog, "descriptionTextBox", description); }
    internal async Task<Window> Child(string name)
    { await PushProcessTestDriver.UntilAsync(Dialog, () => Dialog.OwnedWindows.Any(window => window.Name == name)); return Dialog.OwnedWindows.Single(window => window.Name == name); }
    public async ValueTask DisposeAsync()
    {
        try { Dialog.BeginClose(); Dialog.Close(); await PushProcessTestDriver.CompleteAsync(Completion, Host.Owner); Dialog.DisposeWork(); }
        finally { Host.Dispose(); }
    }
}
