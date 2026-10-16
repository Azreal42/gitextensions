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
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ScriptPreparationDispatcherTests
{
    [AvaloniaTest]
    [TestCase("en", false)]
    [TestCase("fr", true)]
    public async Task WorkerPreparationDispatchesAllFiveRequestsInCoreOrderWithImmediateTypingAndFrozenFacts(string language, bool dark)
    {
        using OwnerFixture f = new(dark); TranslationCatalog catalog = Catalog(language);
        List<ScriptReference> facts = [new("tag-two", ScriptReferenceKind.Tag), new("tag-one", ScriptReferenceKind.Tag),
            new("origin/topic", ScriptReferenceKind.RemoteBranch, "origin"), new("upstream/topic", ScriptReferenceKind.RemoteBranch, "upstream")];
        List<GitReference> metadata = [Ref("tags/tag-two"), Ref("tags/tag-one"), Ref("remotes/origin/topic"), Ref("remotes/upstream/topic")];
        ScriptContext context = Context(facts); ScriptPreparationDispatcher dispatcher = new(f.Owner, context, new(metadata, []), catalog,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        Task<ScriptPreparationResult> preparing = Task.Run(() => dispatcher.PrepareAsync(Script("{UserInput:Label=  default λ  }|{UserFiles}|{sTag}|{sRemote}") with { AskConfirmation = true }));
        try
        {
            Window question = await Child<Window>(f); Pump(question);
            Assert.That(question.ActualThemeVariant, Is.EqualTo(f.Owner.ActualThemeVariant));
            Assert.That(question.Width, Is.EqualTo(520)); Assert.That(question.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text?.Contains("display") == true).Text,
                Does.Contain("display & literal"));
            Capture(question, $"script-dispatch-confirm-{language}-{dark}.png");
            facts.Clear(); metadata.Clear(); // Neither Core nor the UI may reread these caller-owned lists.
            Button yes = question.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == catalog.Translate("Yes"));
            PushProcessTestDriver.Pointer(yes);
            SimplePromptDialog input = await Child<SimplePromptDialog>(f); TextBox text = Field<TextBox>(input, "txtUserInput");
            Assert.That(input.FocusManager!.GetFocusedElement(), Is.SameAs(text)); Assert.That(text.SelectedText, Is.EqualTo("  default λ  "));
            input.KeyTextInput("m"); Pump(input); Assert.That(text.Text, Is.EqualTo("m"));
            input.KeyTextInput("arker λ"); Press(input, PhysicalKey.Enter);
            FilePromptDialog files = await Child<FilePromptDialog>(f); TextBox path = Field<TextBox>(files, "txtFilePath");
            Assert.That(files.FocusManager!.GetFocusedElement(), Is.SameAs(path)); files.KeyTextInput("  exact file λ  ");
            Capture(files, $"script-dispatch-files-{language}-{dark}.png"); Press(files, PhysicalKey.Enter);
            QuickGitRefSelectorDialog references = await Child<QuickGitRefSelectorDialog>(f); ListBox refs = Field<ListBox>(references, "lbxRefs");
            Assert.That(refs.Items.OfType<QuickGitReferenceItem>().Where(item => item.Reference is not null).Select(item => item.Reference!.Name),
                Is.EqualTo(new[] { "refs/tags/tag-one", "refs/tags/tag-two" }));
            Assert.That(Field<Button>(references, "btnAction").Content, Is.EqualTo(catalog.Translate("FormQuickGitRefSelector", "_actionSelect", "Text", "Select")));
            Capture(references, $"script-dispatch-refs-{language}-{dark}.png"); Press(references, PhysicalKey.Enter);
            QuickStringSelectorDialog remotes = await Child<QuickStringSelectorDialog>(f); ListBox list = Field<ListBox>(remotes, "lbxRefs");
            Assert.That(list.Items.OfType<string>(), Is.EqualTo(new[] { "origin", "upstream" }));
            remotes.KeyTextInput("u"); Pump(remotes); Assert.That(remotes.SelectedString, Is.EqualTo("upstream"));
            Capture(remotes, $"script-dispatch-remotes-{language}-{dark}.png"); Press(remotes, PhysicalKey.Enter);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Completed));
            Assert.That(result.Expansion.Arguments, Is.EqualTo("marker λ|  exact file λ  |tag-one|upstream"));
            Assert.That(result.Expansion.Command, Is.EqualTo("fixture-no-launch"));
            Assert.That(result.Responses.Select(response => response.Request.Kind), Is.EqualTo(new[]
            { ScriptRequestKind.Confirmation, ScriptRequestKind.UserInput, ScriptRequestKind.UserFiles, ScriptRequestKind.ChooseReference, ScriptRequestKind.ChooseRemote }));
            Assert.That(result.Responses.All(response => response.Reply.Kind == ScriptReplyKind.Accepted), Is.True);
            Assert.That(result.Responses[3].SelectedReference!.Hash, Is.EqualTo(Hash)); Assert.That(f.Owner.OwnedWindows, Is.Empty);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task ConfirmationNoEscapeAndCloseRemainDistinctAndNeverOpenLaterInputs(int action)
    {
        using OwnerFixture f = new(); Task<ScriptPreparationResult> preparing = CreateDispatcher(f).PrepareAsync(Script("{UserInput}") with { AskConfirmation = true });
        try
        {
            Window question = await Child<Window>(f);
            if (action == 0) { PushProcessTestDriver.Pointer(question.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "No")); }
            else if (action == 1) { Press(question, PhysicalKey.Escape); }
            else { question.Close(); }
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Status, Is.EqualTo(action == 0 ? ScriptExpansionStatus.Declined : ScriptExpansionStatus.Cancelled));
            Assert.That(result.Responses.Single().Reply.Kind, Is.EqualTo(action == 0 ? ScriptReplyKind.Rejected : ScriptReplyKind.Cancelled));
            Assert.That(result.Expansion.Command, Is.Null); Assert.That(f.Owner.OwnedWindows, Is.Empty);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task QuickCancelUsesCoreEmptyChoiceWithoutReturningTheSelectedItem(bool remote)
    {
        using OwnerFixture f = new(); var (context, metadata) = Choices();
        Task<ScriptPreparationResult> preparing = new ScriptPreparationDispatcher(f.Owner, context, metadata).PrepareAsync(Script("before{" + (remote ? "sRemote" : "sBranch") + "}after"));
        try
        {
            Window child = remote ? await Child<QuickStringSelectorDialog>(f) : await Child<QuickGitRefSelectorDialog>(f);
            Assert.That(Field<ListBox>(child, "lbxRefs").SelectedItem, Is.Not.Null); Press(child, PhysicalKey.Escape);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Completed)); Assert.That(result.Expansion.Arguments, Is.EqualTo("beforeafter"));
            Assert.That(result.Responses.Single().Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Cancelled)));
            Assert.That(result.Responses.Single().SelectedReference, Is.Null);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task HomonymousBranchesUseCapturedKindsAndNativeIdentityWithoutRenameDeleteEligibility()
    {
        using OwnerFixture f = new(); List<ScriptReference> facts = [new("origin/shared", ScriptReferenceKind.LocalBranch), new("origin/shared", ScriptReferenceKind.RemoteBranch, "origin"), new("origin/shared", ScriptReferenceKind.Tag)];
        GitReference local = Ref("heads/origin/shared") with { IsCurrent = true }, remote = Ref("remotes/origin/shared"), tag = Ref("tags/origin/shared");
        ScriptPreparationDispatcher dispatcher = new(f.Owner, Context(facts), new([local, remote, tag, Ref("heads/outside")], []));
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script("{sBranch}"));
        try
        {
            QuickGitRefSelectorDialog child = await Child<QuickGitRefSelectorDialog>(f); ListBox list = Field<ListBox>(child, "lbxRefs");
            QuickGitReferenceItem[] entries = list.Items.OfType<QuickGitReferenceItem>().Where(item => item.Reference is not null).ToArray();
            Assert.That(entries.Select(item => item.Reference), Is.EqualTo(new[] { local, remote }));
            Assert.That(entries[0].Reference!.IsCurrent, Is.True, "Select must not inherit Rename/Delete eligibility.");
            PushProcessTestDriver.Pointer((Control)list.ContainerFromIndex(3)!); Press(child, PhysicalKey.Enter);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Arguments, Is.EqualTo("origin/shared")); Assert.That(result.Responses.Single().SelectedReference, Is.SameAs(remote));
            Assert.That(result.Responses.Single().SelectedReference!.IsRemote, Is.True);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task AcceptingAReferenceHeaderIsAnExplicitAcceptedEmptyChoice()
    {
        using OwnerFixture f = new(); var (context, metadata) = Choices();
        Task<ScriptPreparationResult> preparing = new ScriptPreparationDispatcher(f.Owner, context, metadata).PrepareAsync(Script("{sBranch}"));
        try
        {
            QuickGitRefSelectorDialog child = await Child<QuickGitRefSelectorDialog>(f); Press(child, PhysicalKey.ArrowUp); Press(child, PhysicalKey.Enter);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Arguments, Is.Empty); Assert.That(result.Responses.Single().Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Accepted, "")));
            Assert.That(result.Responses.Single().SelectedReference, Is.Null);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task InputAndFilesCancelStopPreparationBeforeAnyLaterRequest(bool files)
    {
        using OwnerFixture f = new(); Task<ScriptPreparationResult> preparing = CreateDispatcher(f).PrepareAsync(Script(files ? "{UserFiles}|{sTag}" : "{UserInput}|{UserFiles}"));
        try
        {
            Window child = files ? await Child<FilePromptDialog>(f) : await Child<SimplePromptDialog>(f); Press(child, PhysicalKey.Escape);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Cancelled)); Assert.That(result.Expansion.Command, Is.Null);
            Assert.That(result.Responses.Count, Is.EqualTo(1)); Assert.That(result.Responses[0].Reply.Kind, Is.EqualTo(ScriptReplyKind.Cancelled));
            Assert.That(f.Owner.OwnedWindows, Is.Empty);
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task ACurrentReferenceRequestUsesOnlyTheCurrentSnapshot()
    {
        using OwnerFixture f = new(); ScriptContext context = Context([]) with { CurrentReferences = [new("current-two", ScriptReferenceKind.LocalBranch), new("current-one", ScriptReferenceKind.LocalBranch)], CurrentRemote = "origin" };
        ScriptPreparationDispatcher dispatcher = new(f.Owner, context, new([Ref("heads/selected")], [Ref("heads/current-two"), Ref("heads/current-one")]));
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script("{cBranch}"));
        try
        {
            QuickGitRefSelectorDialog child = await Child<QuickGitRefSelectorDialog>(f);
            Assert.That(Field<ListBox>(child, "lbxRefs").Items.OfType<QuickGitReferenceItem>().Where(item => item.Reference is not null).Select(item => item.Reference!.ShortName),
                Is.EqualTo(new[] { "current-one", "current-two" }));
            Press(child, PhysicalKey.Enter); ScriptPreparationResult result = await Finish(preparing); Assert.That(result.Expansion.Arguments, Is.EqualTo("current-one"));
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationOrOwnerCloseDrainsAnOutstandingBrowseAndRejectsItsLateReply(bool ownerClose)
    {
        using OwnerFixture f = new(); using CancellationTokenSource token = new(); TaskCompletionSource<IReadOnlyList<string>> chosen = new();
        CancellationToken pickerToken = default; int browseCalls = 0;
        ScriptPreparationDispatcher dispatcher = new(f.Owner, Context([]), new([], []), browseFiles: (initial, ct) =>
        { Assert.That(initial, Is.EqualTo(".")); Assert.That(Dispatcher.UIThread.CheckAccess(), Is.True); pickerToken = ct; browseCalls++; return chosen.Task; });
        Task<ScriptPreparationResult> preparing = Task.Run(() => dispatcher.PrepareAsync(Script("{UserFiles}|{UserInput}"), token.Token));
        try
        {
            // Core resolves UserInput before UserFiles, independent of their textual order.
            SimplePromptDialog input = await Child<SimplePromptDialog>(f); input.KeyTextInput("accepted before browse"); Press(input, PhysicalKey.Enter);
            FilePromptDialog child = await Child<FilePromptDialog>(f); PushProcessTestDriver.Pointer(Field<Button>(child, "btnBrowse"));
            await Until(() => browseCalls == 1); Assert.That(child.IsBrowsing, Is.True);
            if (ownerClose) { f.Owner.Close(); } else { token.Cancel(); }
            await Until(() => !child.IsVisible); Assert.That(pickerToken.IsCancellationRequested, Is.True);
            Assert.That(preparing.IsCompleted, Is.False, "Do not expose a result while BrowseCompletion is still pending.");
            chosen.SetResult([f.File("late λ.txt")]); await Until(() => preparing.IsCompleted);
            Assert.ThrowsAsync<OperationCanceledException>(async () => await preparing);
            Assert.That(child.WasAccepted, Is.False); Assert.That(child.UserInput, Is.Empty); Assert.That(child.IsBrowsing, Is.False);
            Assert.That(Field<TextBox>(child, "txtFilePath").Text, Is.Null.Or.Empty); Assert.That(f.Owner.OwnedWindows, Is.Empty);
        }
        finally { chosen.TrySetResult([]); token.Cancel(); await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task AcceptingFilesWhileBrowseIsPendingWaitsForDrainAndPreservesTheAcceptedLiteral()
    {
        using OwnerFixture f = new(); TaskCompletionSource<IReadOnlyList<string>> chosen = new();
        ScriptPreparationDispatcher dispatcher = new(f.Owner, Context([]), new([], []), browseFiles: (_, _) => chosen.Task);
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script("{UserFiles}"));
        try
        {
            FilePromptDialog child = await Child<FilePromptDialog>(f); child.KeyTextInput(" exact accepted "); PushProcessTestDriver.Pointer(Field<Button>(child, "btnBrowse"));
            PushProcessTestDriver.Pointer(Field<Button>(child, "btnOk")); await Until(() => !child.IsVisible);
            Assert.That(preparing.IsCompleted, Is.False); chosen.SetResult([f.File("late.txt")]);
            ScriptPreparationResult result = await Finish(preparing);
            Assert.That(result.Expansion.Arguments, Is.EqualTo(" exact accepted ")); Assert.That(child.UserInput, Is.EqualTo(" exact accepted "));
        }
        finally { chosen.TrySetResult([]); await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task SuccessfulBrowseFeedsItsQuotedExistingPathsThroughTheRealTemplateWithoutAnotherEscapingPass()
    {
        using OwnerFixture f = new(); string[] paths = [f.File("one λ.txt"), f.File("two\tline\nquote\".txt")];
        ScriptPreparationDispatcher dispatcher = new(f.Owner, Context([]), new([], []), browseFiles: (_, _) => Task.FromResult<IReadOnlyList<string>>(paths));
        Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script("{UserFiles}"));
        try
        {
            FilePromptDialog child = await Child<FilePromptDialog>(f); PushProcessTestDriver.Pointer(Field<Button>(child, "btnBrowse"));
            await Until(() => !child.IsBrowsing); string literal = string.Join(' ', paths.Select(ScriptInputDialogControls.QuoteFilePath));
            Assert.That(Field<TextBox>(child, "txtFilePath").Text, Is.EqualTo(literal)); Press(child, PhysicalKey.Enter);
            Assert.That((await Finish(preparing)).Expansion.Arguments, Is.EqualTo(literal));
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task EmptySimpleInputIsAcceptedButEmptyFileInputCancelsTheFollowingRequest()
    {
        using OwnerFixture f = new(); Task<ScriptPreparationResult> preparing = CreateDispatcher(f).PrepareAsync(Script("{UserInput}|{UserFiles}"));
        try
        {
            SimplePromptDialog input = await Child<SimplePromptDialog>(f); Press(input, PhysicalKey.Enter);
            FilePromptDialog files = await Child<FilePromptDialog>(f); Press(files, PhysicalKey.Enter);
            ScriptPreparationResult result = await Finish(preparing); Assert.That(result.Expansion.Status, Is.EqualTo(ScriptExpansionStatus.Cancelled));
            Assert.That(result.Responses.Select(response => response.Reply), Is.EqualTo(new[] { new ScriptReply(ScriptReplyKind.Accepted, ""), new ScriptReply(ScriptReplyKind.Cancelled) }));
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    public async Task MissingMetadataFailsClosedInsteadOfInferringAReferenceTypeFromItsName()
    {
        using OwnerFixture f = new(); ScriptContext context = Context([new("origin/topic", ScriptReferenceKind.LocalBranch), new("other", ScriptReferenceKind.LocalBranch)]);
        Task<ScriptPreparationResult> preparing = new ScriptPreparationDispatcher(f.Owner, context, new([Ref("remotes/origin/topic"), Ref("heads/other")], [])).PrepareAsync(Script("{sBranch}"));
        await Until(() => preparing.IsCompleted); Assert.ThrowsAsync<InvalidOperationException>(async () => await preparing); Assert.That(f.Owner.OwnedWindows, Is.Empty);
    }

    [AvaloniaTest]
    public async Task PreCancellationAndConcurrentPreparationDoNotOpenAdditionalWindowsAndSessionCanBeReused()
    {
        using OwnerFixture f = new(); ScriptPreparationDispatcher dispatcher = CreateDispatcher(f); using CancellationTokenSource token = new(); token.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await dispatcher.PrepareAsync(Script("{UserInput}"), token.Token));
        Assert.That(f.Owner.OwnedWindows, Is.Empty); Task<ScriptPreparationResult> preparing = dispatcher.PrepareAsync(Script("{UserInput}"));
        try
        {
            SimplePromptDialog child = await Child<SimplePromptDialog>(f);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.PrepareAsync(Script("{UserFiles}")));
            Assert.That(f.Owner.OwnedWindows.Count, Is.EqualTo(1)); child.KeyTextInput("first"); Press(child, PhysicalKey.Enter); Assert.That((await Finish(preparing)).Expansion.Arguments, Is.EqualTo("first"));
            preparing = dispatcher.PrepareAsync(Script("{UserInput}")); child = await Child<SimplePromptDialog>(f); child.KeyTextInput("second"); Press(child, PhysicalKey.Enter);
            Assert.That((await Finish(preparing)).Expansion.Arguments, Is.EqualTo("second"));
        }
        finally { await Stop(f, preparing); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task HistoricalBoolConfirmationKeepsItsDefaultAndFalseForEscapeAndNo(bool escape)
    {
        using OwnerFixture f = new(); Task<bool> question = Dialogs.ConfirmAsync(f.Owner, "Original title", "Original message", "Accept", "Cancel");
        Window child = await Child<Window>(f); Button cancel = child.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Cancel");
        Assert.That(cancel.IsDefault, Is.True); Assert.That(cancel.IsCancel, Is.True); Assert.That(child.Width, Is.EqualTo(520));
        if (escape) { Press(child, PhysicalKey.Escape); } else { PushProcessTestDriver.Pointer(cancel); }
        await Until(() => question.IsCompleted); Assert.That(await question, Is.False);
        question = Dialogs.ConfirmAsync(f.Owner, "Original title", "Original message", "Accept", "Cancel", true); child = await Child<Window>(f);
        Assert.That(child.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Accept").IsDefault, Is.True);
        Press(child, PhysicalKey.Enter); await Until(() => question.IsCompleted); Assert.That(await question, Is.True);
    }

    private const string Hash = "1111111111111111111111111111111111111111";
    private static GitReference Ref(string name) => new("refs/" + name, Hash, null, false);
    private static ScriptDefinition Script(string arguments) => new() { Name = "display && literal", Command = "fixture-no-launch", Arguments = arguments };
    private static ScriptContext Context(IReadOnlyList<ScriptReference> references) => new()
    { HasRevisionGrid = true, SelectedRevision = new(Hash), CurrentRevision = new(Hash), SelectedReferences = references, CurrentRemote = "origin" };
    private static ScriptPreparationDispatcher CreateDispatcher(OwnerFixture fixture) => new(fixture.Owner, Context([]), new([], []));
    private static (ScriptContext, ScriptReferenceMetadata) Choices() => (Context([new("local", ScriptReferenceKind.LocalBranch), new("other", ScriptReferenceKind.LocalBranch),
        new("origin/topic", ScriptReferenceKind.RemoteBranch, "origin"), new("upstream/topic", ScriptReferenceKind.RemoteBranch, "upstream")]),
        new([Ref("heads/local"), Ref("heads/other"), Ref("remotes/origin/topic"), Ref("remotes/upstream/topic")], []));
    private static TranslationCatalog Catalog(string language) => TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo(language));
    private static async Task<T> Child<T>(OwnerFixture fixture) where T : Window
    { await Until(() => fixture.Owner.OwnedWindows.OfType<T>().Any(window => window.IsVisible)); T child = fixture.Owner.OwnedWindows.OfType<T>().Single(window => window.IsVisible); Pump(child); return child; }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready)
    { Stopwatch watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(ready(), Is.True); }
    private static async Task<ScriptPreparationResult> Finish(Task<ScriptPreparationResult> task) { await Until(() => task.IsCompleted); return await task; }
    private static async Task Stop(OwnerFixture fixture, Task task)
    { foreach (Window child in fixture.Owner.OwnedWindows.ToArray()) { child.Close(); } await Until(() => task.IsCompleted); try { await task; } catch (OperationCanceledException) { } }
    private static void Capture(TopLevel root, string name)
    { Pump(root); using var image = root.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null); string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path); }
    private sealed class OwnerFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "script-preparation-" + Guid.NewGuid().ToString("N"));
        internal Window Owner { get; } = new() { Width = 900, Height = 650 };
        internal OwnerFixture(bool dark = false) { Directory.CreateDirectory(_directory); Owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Owner.Show(); Pump(Owner); }
        internal string File(string name) { string path = Path.Combine(_directory, name); System.IO.File.WriteAllText(path, "fixture\n"); return path; }
        public void Dispose() { Owner.Close(); if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
    }
}
