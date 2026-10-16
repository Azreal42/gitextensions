using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.PullAdvancedDialogTests.Fixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class PullLayoutTests
{
    [AvaloniaTest]
    public async Task WindowsReferenceLayoutShowsOriginalHelpGroupsAndFooterInBothThemesAndAllModes()
    {
        await using Fixture f = await Fixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            dialog.RequestedThemeVariant = theme;
            foreach ((string radio, string asset) in new[] { ("PullFetch", "HelpPullFetch.png"), ("PullMerge", "HelpPullMerge.png"), ("PullRebase", "HelpPullRebase.png") })
            {
                Click(dialog, radio); Pump(dialog);
                Assert.That(Field<Image>(dialog, "OriginalCommandHelpImage").Tag, Is.EqualTo(asset));
                Control help = Field<Control>(dialog, "PullHelp"), source = Field<Control>(dialog, "PullSourceGroup");
                Assert.That(Position(help, dialog).X + help.Bounds.Width, Is.LessThan(Position(source, dialog).X));
                string[] names = ["PullSourceGroup", "PullBranchGroup", "PullMergeGroup", "PullTagsGroup"];
                double[] positions = names.Select(name => Position(Field<Control>(dialog, name), dialog).Y).ToArray();
                Assert.That(positions, Is.Ordered.Ascending);
                Assert.That(Field<TextBox>(dialog, "PullOutput").IsVisible, Is.False);
                foreach (string name in new[] { "ResolvePullConflict", "PullStashChanges", "PullAutoStash", "ExecutePull" })
                {
                    Control control = Field<Control>(dialog, name); Point p = Position(control, dialog);
                    Assert.That(control.IsEffectivelyVisible, Is.True, name);
                    Assert.That(p.Y + control.Bounds.Height, Is.LessThanOrEqualTo(dialog.ClientSize.Height), name);
                }
                Assert.That(Position(Field<Button>(dialog, "ResolvePullConflict"), dialog).X, Is.EqualTo(Position(source, dialog).X).Within(1));
                Assert.That(Field<ComboBox>(dialog, "PullRemote").IsEditable && Field<ComboBox>(dialog, "PullBranch").IsEditable, Is.True);
                Assert.That(Field<RadioButton>(dialog, "PullFetch").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(part => part.Name == "OuterEllipse").Bounds.Width, Is.EqualTo(13));
                Assert.That(Field<CheckBox>(dialog, "PullPrune").GetVisualDescendants().OfType<Border>().Single(part => part.Name == "NormalRectangle").Bounds.Width, Is.EqualTo(13));
                Assert.That(Field<TextBox>(dialog, "PullLocalBranch").Text, Is.EqualTo(radio == "PullFetch" ? "" : "main"));
                if (theme == ThemeVariant.Light && radio == "PullFetch")
                {
                    string tree = System.Text.Json.JsonSerializer.Serialize(new[] { "PullFetch", "PullPrune" }.Select(name => new
                    {
                        name, parts = Field<Control>(dialog, name).GetVisualDescendants().OfType<Control>().Select(part => new
                        { type = part.GetType().Name, part.Name, bounds = part.Bounds.ToString(), margin = part.Margin.ToString(), parent = (part.Parent as Control)?.Name })
                    }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pull-toggle-template-parts.json");
                    File.WriteAllText(path, tree); TestContext.AddTestAttachment(path);
                }
                Capture(dialog, "pull-reference-" + radio + "-" + theme + ".png");
            }
        }
        dialog.Close();
    }

    [AvaloniaTest]
    public async Task HelpToggleIsRememberedAcrossModeChangesAndReopeningAndTheRemoteDropdownWorks()
    {
        await using Fixture f = await Fixture.CreateAsync(); PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        double expanded = dialog.Width;
        Click(dialog, "OriginalCommandHelpToggle"); Pump(dialog);
        Assert.That(Field<OriginalCommandHelp>(dialog, "OriginalCommandHelp").IsExpanded, Is.False);
        Assert.That(dialog.Width, Is.LessThan(expanded));
        Click(dialog, "PullMerge"); Pump(dialog);
        Assert.That(Field<OriginalCommandHelp>(dialog, "OriginalCommandHelp").IsExpanded, Is.False);
        ComboBox remote = Field<ComboBox>(dialog, "PullRemote");
        Point arrow = remote.TranslatePoint(new Point(remote.Bounds.Width - 8, remote.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(arrow, MouseButton.Left); dialog.MouseUp(arrow, MouseButton.Left); Pump(dialog);
        Assert.That(remote.IsDropDownOpen, Is.True); remote.IsDropDownOpen = false;
        dialog.Close();
        PullDialog reopened = await f.OpenAsync(GitPullAction.Fetch);
        Assert.That(Field<OriginalCommandHelp>(reopened, "OriginalCommandHelp").IsExpanded, Is.False);
        Click(reopened, "OriginalCommandHelpToggle"); Pump(reopened);
        Assert.That(Field<OriginalCommandHelp>(reopened, "OriginalCommandHelp").IsExpanded, Is.True);
        Capture(reopened, "pull-help-restored.png"); reopened.Close();
    }

    [AvaloniaTest]
    public async Task UrlFetchCreatesTheChosenLocalBranchWithoutCheckingItOutOrChangingAnotherRef()
    {
        await using Fixture f = await Fixture.CreateAsync(); string remoteHash = await f.AdvanceAsync("remote.txt");
        string source = f.Root.Output("source [literal] λ.git");
        await f.Git(f.Server, "clone", "--bare", f.Server.Path, source);
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch); Click(dialog, "PullFromUrl");
        Field<ComboBox>(dialog, "PullUrl").Text = source;
        Field<ComboBox>(dialog, "PullBranch").Text = "main";
        Field<TextBox>(dialog, "PullLocalBranch").Text = "fetched/é-λ";
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.Repository.ResolveRevisionAsync("fetched/é-λ"), Is.EqualTo(remoteHash));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That(await f.Repository.ResolveRevisionAsync("origin/main"), Is.EqualTo(f.Initial));
        Assert.That((await f.Repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
        Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(new CloneSourcesStore().Load(), Does.Contain(source)); dialog.Close();
    }

    [AvaloniaTest]
    public async Task UrlPullUsesTheSelectedSourceAndRemoteBranchAndLeavesTheConfiguredRemoteUnchanged()
    {
        await using Fixture f = await Fixture.CreateAsync(); string remoteHash = await f.AdvanceAsync("remote.txt");
        PullDialog dialog = await f.OpenAsync(); Click(dialog, "PullFromUrl");
        Field<ComboBox>(dialog, "PullUrl").Text = f.Server.Path;
        Field<ComboBox>(dialog, "PullBranch").Text = "main";
        Assert.That(Field<Button>(dialog, "PullBrowse").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "PullManageRemotes").IsEnabled, Is.False);
        Assert.That(Field<TextBox>(dialog, "PullLocalBranch").IsEnabled, Is.False);
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(remoteHash));
        Assert.That((await f.Repository.GetRemotesAsync()).Single().Name, Is.EqualTo("origin")); dialog.Close();
    }

    [AvaloniaTest]
    public async Task FetchIntoTheCheckedOutBranchIsRejectedBeforeAnyGitMutation()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.AdvanceAsync("remote.txt");
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Field<ComboBox>(dialog, "PullBranch").Text = "main"; Field<TextBox>(dialog, "PullLocalBranch").Text = "main";
        string refs = (await f.Git(f.Repository, "show-ref")).StandardOutput;
        await dialog.ExecuteAsync(dialog);
        Assert.That(dialog.LastException, Is.TypeOf<InvalidOperationException>());
        Assert.That(dialog.Attempted, Is.False); Assert.That(dialog.LastProcess, Is.Null);
        Assert.That((await f.Git(f.Repository, "show-ref")).StandardOutput, Is.EqualTo(refs));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial)); dialog.Close();
    }

    [AvaloniaTest]
    public async Task ResettingAnExistingFetchDestinationRequiresConfirmationAndCancelPreservesAllRefs()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.Git(f.Repository, "branch", "saved", f.Initial);
        string remote = await f.AdvanceAsync("remote.txt");
        PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Field<ComboBox>(dialog, "PullBranch").Text = "main"; Field<TextBox>(dialog, "PullLocalBranch").Text = "saved";
        string refs = (await f.Git(f.Repository, "show-ref")).StandardOutput;
        Task cancelled = dialog.ExecuteAsync(dialog);
        await Until(() => dialog.OwnedWindows.Any()); Window prompt = dialog.OwnedWindows.Single();
        Button cancel = prompt.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel);
        Assert.That(cancel.IsDefault, Is.True); Click(prompt, cancel); await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(dialog.Cancelled, Is.True); Assert.That(dialog.Attempted, Is.False);
        Assert.That((await f.Git(f.Repository, "show-ref")).StandardOutput, Is.EqualTo(refs));
        Task accepted = dialog.ExecuteAsync(dialog); await Until(() => dialog.OwnedWindows.Any()); prompt = dialog.OwnedWindows.Single();
        Click(prompt, prompt.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
        await accepted.WaitAsync(TimeSpan.FromSeconds(10)); Assert.That(dialog.Succeeded, Is.True, dialog.LastError);
        Assert.That(await f.Repository.ResolveRevisionAsync("saved"), Is.EqualTo(remote));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial));
        Assert.That((await f.Repository.GetStatusAsync()).Changes, Is.Empty); dialog.Close();
    }

    [AvaloniaTest]
    public async Task EmptyUrlDisablesExecutionAndReturningToRemoteRestoresItsActualUrl()
    {
        await using Fixture f = await Fixture.CreateAsync(); PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Click(dialog, "PullFromUrl"); Field<ComboBox>(dialog, "PullUrl").Text = "";
        Assert.That(Field<Button>(dialog, "ExecutePull").IsEnabled, Is.False);
        await dialog.ExecuteAsync(dialog); Assert.That(dialog.Attempted, Is.False);
        Click(dialog, "PullFromRemote");
        Assert.That(Field<ComboBox>(dialog, "PullUrl").Text, Is.EqualTo(f.Server.Path));
        Assert.That(Field<Button>(dialog, "ExecutePull").IsEnabled, Is.True);
        Assert.That(Field<Button>(dialog, "PullBrowse").IsEnabled, Is.False);
        Assert.That(Field<Button>(dialog, "PullManageRemotes").IsEnabled, Is.True); dialog.Close();
    }

    [AvaloniaTest]
    public async Task ManageRemotesReloadsTheActualRemoteNameAndUrlAndDoesNotChangeHead()
    {
        await using Fixture f = await Fixture.CreateAsync(); PullDialog dialog = await f.OpenAsync(GitPullAction.Fetch);
        Click(dialog, "PullManageRemotes");
        await Until(() => dialog.OwnedWindows.OfType<RemotesDialog>().Any(window => !window.IsBusy));
        RemotesDialog manager = dialog.OwnedWindows.OfType<RemotesDialog>().Single();
        Field<TextBox>(manager, "RemoteSettingsName").Text = "upstream";
        Click(manager, "SaveRemoteSettings"); await PushProcessTestDriver.UntilAsync(manager, () => !manager.IsBusy);
        Assert.That(manager.LastError, Is.Null); manager.Close(); await Until(() => !dialog.IsBusy);
        Assert.That(Field<ComboBox>(dialog, "PullRemote").Text, Is.EqualTo("upstream"));
        Assert.That(Field<ComboBox>(dialog, "PullUrl").Text, Is.EqualTo(f.Server.Path));
        Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(f.Initial)); dialog.Close();
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Point Position(Control control, Window root) => control.TranslatePoint(default, root)!.Value;
    private static void Pump(Window root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Window root, string name) => Click(root, Field<Control>(root, name));
    private static void Click(Window root, Control control)
    {
        Pump(root); Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, control.Name);
        Point p = control.TranslatePoint(new Point(Math.Min(12, control.Bounds.Width / 2), control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(root);
    }
    private static async Task Until(Func<bool> condition)
    { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) { await Task.Delay(20); } Assert.That(condition(), Is.True); }
    private static void Capture(Window root, string name)
    { Pump(root); using WriteableBitmap frame = root.CaptureRenderedFrame()!; string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path); }
}
