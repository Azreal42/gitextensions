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
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GenericPromptHostTests
{
    [AvaloniaTest, TestCase(false), TestCase(true)]
    public async Task FrenchStringSelectorUsesTheOriginalCommonSelectCaptionAndRealAcceptance(bool dark)
    {
        Window owner = new() { Width = 800, Height = 600, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; owner.Show();
        TranslationCatalog french = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("fr"));
        QuickStringSelectorDialog dialog = new(["origin", "upstream"], french) { RequestedThemeVariant = owner.ActualThemeVariant };
        Task closed = dialog.ShowDialog(owner);
        try
        {
            Pump(dialog); Assert.That(Field<Button>(dialog, "btnAction").Content, Is.EqualTo("Sélectionner"));
            Assert.That(dialog.Title, Is.EqualTo("Sélectionner")); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(owner.ActualThemeVariant));
            Assert.That(Field<ListBox>(dialog, "lbxRefs").IsKeyboardFocusWithin, Is.True);
            PushProcessTestDriver.Pointer(Field<Button>(dialog, "btnAction")); await closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.Reply, Is.EqualTo(new ScriptReply(ScriptReplyKind.Accepted, "origin"))); Assert.That(owner.IsVisible, Is.True);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task RealBrowseScriptMenuAnchorsItsRemoteSelectorAtTheCapturedGraphCellAndKeepsAUserMove()
    {
        await using Host h = await Host.CreateAsync(remotes: true); ListBox history = Field<ListBox>(h.Main, "RevisionHistory");
        CommitGraph graph = h.Main.GetVisualDescendants().OfType<CommitGraph>().Single(); Assert.That(graph.IsEffectivelyVisible, Is.True);
        ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(history.SelectedIndex)!; row.Focus(); Pump(h.Main);
        Point bottom = row.TranslatePoint(new Point(0, row.Bounds.Height), graph)!.Value;
        PixelPoint anchor = graph.PointToScreen(new Point(graph.Bounds.Width, bottom.Y));
        h.OpenScriptMenu(); QuickStringSelectorDialog dialog = await Child<QuickStringSelectorDialog>(h.Main);
        Task work = h.Main.WindowWorkCompletion;
        try
        {
            var area = h.Main.Screens.ScreenFromPoint(anchor)!.WorkingArea;
            Assert.That(anchor.X + dialog.Width * dialog.RenderScaling, Is.LessThanOrEqualTo(area.Right), "This fixture keeps its graph-cell anchor away from an edge.");
            Assert.That(anchor.Y + dialog.Height * dialog.RenderScaling, Is.LessThanOrEqualTo(area.Bottom));
            Assert.That(dialog.Position, Is.EqualTo(anchor), "The real Script caller must pass the graph-cell anchor rather than owner+(12,32).");
            PixelPoint moved = new(dialog.Position.X + 17, dialog.Position.Y + 11); dialog.Position = moved;
            Pump(dialog); Pump(h.Main); Assert.That(dialog.Position, Is.EqualTo(moved), "A one-time placement must not chase a later user move.");
            Press(dialog, PhysicalKey.Enter); await PushProcessTestDriver.CompleteAsync(work, h.Main);
            Assert.That(h.Main.LastException, Is.Null); Assert.That(h.Main.OwnedWindows, Is.Empty);
            Assert.That((await h.Git.GitAsync("config", "--local", "--get", "promptfixture.selectedremote")).StandardOutput.Trim(), Is.EqualTo("origin"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task SharedPlacementFitsTheMappedSelectorOnceWhileAnUnattachedScriptHostKeepsItsFallback()
    {
        Window owner = new() { Width = 700, Height = 500, Position = new PixelPoint(20, 20), WindowStartupLocation = WindowStartupLocation.Manual }; owner.Show();
        ScriptContext context = RemoteContext(); var area = owner.Screens.ScreenFromWindow(owner)!.WorkingArea;
        PixelPoint edge = new(area.Right - 1, area.Bottom - 1); int placements = 0;
        ScriptPreparationDispatcher placed = new(owner, context, new([], []),
            selectorPlacement: dialog => { placements++; return MainWindow.FitQuickSelectorLocation(owner, dialog, edge); });
        Task<ScriptPreparationResult> preparing = placed.PrepareAsync(PromptScript());
        try
        {
            QuickStringSelectorDialog dialog = await Child<QuickStringSelectorDialog>(owner);
            Assert.That(placements, Is.EqualTo(1), "Fit only after the mapped selector has calculated its metrics.");
            Assert.That(dialog.Position.X, Is.GreaterThanOrEqualTo(area.X)); Assert.That(dialog.Position.Y, Is.GreaterThanOrEqualTo(area.Y));
            Assert.That(dialog.Position.X + dialog.Width * dialog.RenderScaling, Is.LessThanOrEqualTo(area.Right));
            Assert.That(dialog.Position.Y + dialog.Height * dialog.RenderScaling, Is.LessThanOrEqualTo(area.Bottom));
            PixelPoint moved = new(area.X + 30, area.Y + 30); dialog.Position = moved; Pump(dialog); Assert.That(dialog.Position, Is.EqualTo(moved)); Assert.That(placements, Is.EqualTo(1));
            Press(dialog, PhysicalKey.Escape); await preparing.WaitAsync(TimeSpan.FromSeconds(5));
            ScriptPreparationDispatcher fallback = new(owner, context, new([], [])); preparing = fallback.PrepareAsync(PromptScript());
            dialog = await Child<QuickStringSelectorDialog>(owner);
            Assert.That(dialog.Position, Is.EqualTo(owner.PointToScreen(new Point(12, 32))));
            Press(dialog, PhysicalKey.Escape); await preparing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(owner.IsVisible, Is.True);
        }
        finally { foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } await preparing.WaitAsync(TimeSpan.FromSeconds(5)); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task OwnerCloseDuringTheRealScriptRemoteChoiceCannotLaunchTheFollowingCommand()
    {
        await using Host h = await Host.CreateAsync(remotes: true); h.OpenScriptMenu();
        QuickStringSelectorDialog dialog = await Child<QuickStringSelectorDialog>(h.Main); Task work = h.Main.WindowWorkCompletion;
        h.Main.Close(); await work.WaitAsync(TimeSpan.FromSeconds(15)); await Until(h.Main, () => !h.Main.IsVisible);
        Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.WasAccepted, Is.False);
        Assert.That(h.Main.OwnedWindows.OfType<GitProcessDialog>(), Is.Empty);
        Assert.That(File.ReadAllText(h.Git.PathInRepository(".git/config")), Does.Not.Contain("selectedremote"), "The local command marker must remain absent after owner cancellation.");
        Assert.That((await h.Git.GitAsync("status", "--porcelain")).StandardOutput, Is.Empty);
    }

    [AvaloniaTest]
    public async Task RealMoveMenuUsesTheCompactPromptAndKeepsItsDraftAfterANativeErrorUntilRetrySucceeds()
    {
        await using Host h = await Host.CreateAsync(); string occupied = h.Git.PathInRepository("occupied.txt"); File.WriteAllText(occupied, "keep occupied");
        await h.OpenMoveMenuAsync(); SimplePromptDialog first = await Child<SimplePromptDialog>(h.Main);
        Assert.That(first.ClientSize, Is.EqualTo(new Size(334, 76))); Assert.That(first.Owner, Is.SameAs(h.Main));
        Assert.That(first.ActualThemeVariant, Is.EqualTo(h.Main.ActualThemeVariant)); Assert.That(first.GetVisualDescendants().OfType<Button>().Count(), Is.EqualTo(1));
        Assert.That(Field<TextBox>(first, "txtUserInput").SelectedText, Is.EqualTo("file.txt"));
        Type(first, "occupied.txt"); Press(first, PhysicalKey.Enter);
        Window error = await Message(h.Main); Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Does.Contain("occupied"));
        Assert.That(File.ReadAllText(occupied), Is.EqualTo("keep occupied")); Assert.That(File.Exists(h.Git.File), Is.True);
        PushProcessTestDriver.Pointer(Field<Button>(error, "MessageDialogAccept"));
        SimplePromptDialog retry = await Child<SimplePromptDialog>(h.Main); Assert.That(retry, Is.Not.SameAs(first));
        Assert.That(Field<TextBox>(retry, "txtUserInput").SelectedText, Is.EqualTo("occupied.txt"));
        Type(retry, "renamed.txt"); Press(retry, PhysicalKey.Enter); await Until(h.Main, () => !h.Main.IsBusy && h.Main.OwnedWindows.Count == 0);
        Assert.That(File.Exists(h.Git.File), Is.False); Assert.That(File.ReadAllText(h.Git.PathInRepository("renamed.txt")), Is.EqualTo("second version\n"));
        Assert.That(File.ReadAllText(occupied), Is.EqualTo("keep occupied")); Assert.That(h.Main.LastException, Is.Null);
        Assert.That((await h.Git.GitAsync("diff", "--cached", "--name-status")).StandardOutput, Does.Contain("R100\tfile.txt\trenamed.txt"));
    }

    [AvaloniaTest, TestCase("file.txt"), TestCase("")]
    public async Task InvalidMoveNamesReopenAFreshWindowWithTheLiteralDraftAndCancelWithoutAnyWrite(string draft)
    {
        await using Host h = await Host.CreateAsync(); byte[] index = File.ReadAllBytes(h.Git.PathInRepository(".git/index"));
        await h.OpenMoveMenuAsync(); SimplePromptDialog first = await Child<SimplePromptDialog>(h.Main); Type(first, draft); Press(first, PhysicalKey.Enter);
        SimplePromptDialog retry = await Child<SimplePromptDialog>(h.Main); Assert.That(retry, Is.Not.SameAs(first));
        Assert.That(Field<TextBox>(retry, "txtUserInput").Text, Is.EqualTo(draft));
        CancelInput(retry); await Until(h.Main, () => !h.Main.IsBusy && h.Main.OwnedWindows.Count == 0);
        Assert.That(File.ReadAllBytes(h.Git.PathInRepository(".git/index")), Is.EqualTo(index)); Assert.That(File.Exists(h.Git.File), Is.True);
        Assert.That(h.Main.LastException, Is.Null);
    }

    [AvaloniaTest, TestCase(false), TestCase(true)]
    public async Task RealMoveCancellationOrOwnerCloseDoesNotPublishThePreservedDefault(bool closeOwner)
    {
        await using Host h = await Host.CreateAsync(); byte[] index = File.ReadAllBytes(h.Git.PathInRepository(".git/index"));
        await h.OpenMoveMenuAsync(); SimplePromptDialog prompt = await Child<SimplePromptDialog>(h.Main);
        if (closeOwner) { h.Main.Close(); } else { CancelInput(prompt); }
        await Until(h.Main, () => !h.Main.IsBusy && !prompt.IsVisible && (!closeOwner || !h.Main.IsVisible));
        Assert.That(prompt.WasAccepted, Is.False); Assert.That(File.Exists(h.Git.File), Is.True);
        Assert.That(File.ReadAllBytes(h.Git.PathInRepository(".git/index")), Is.EqualTo(index));
    }

    [AvaloniaTest, TestCase(false), TestCase(true)]
    public async Task MoveRevalidatesItsCapturedSelectionAndCancelsItsOwnedInputBeforeAnyNativeWrite(bool cancelToken)
    {
        using GitFixture git = await GitFixture.CreateAsync(); Window owner = new() { Width = 900, Height = 650 }; owner.Show();
        using CancellationTokenSource cancellation = new(); bool current = true; byte[] index = File.ReadAllBytes(git.PathInRepository(".git/index"));
        FileMenuOperations operations = new(owner, (_, action, _) => action(cancellation.Token), _ => current,
            _ => throw new AssertionException("Move cannot launch an external program."), cancellation.Token);
        FileMenuSelection selection = new(git.Repository, [new(new("file.txt", 'M'), null, new("HEAD"))], ["file.txt"]);
        Task moving = operations.ExecuteAsync(FileMenuCommand.RenameMove, selection);
        try
        {
            SimplePromptDialog prompt = await Child<SimplePromptDialog>(owner);
            if (cancelToken) { cancellation.Cancel(); await Until(owner, () => moving.IsCompleted); Assert.ThrowsAsync<OperationCanceledException>(async () => await moving.WaitAsync(TimeSpan.FromSeconds(5))); }
            else
            {
                current = false; Type(prompt, "must-not-move.txt"); Press(prompt, PhysicalKey.Enter); await Until(owner, () => moving.IsCompleted);
                Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => await moving.WaitAsync(TimeSpan.FromSeconds(5)))!.Message, Does.Contain("selection has changed"));
            }
            Assert.That(prompt.IsVisible, Is.False); Assert.That(owner.IsVisible, Is.True);
            Assert.That(File.ReadAllBytes(git.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(File.Exists(git.File), Is.True); Assert.That(File.Exists(git.PathInRepository("must-not-move.txt")), Is.False);
        }
        finally { cancellation.Cancel(); foreach (Window child in owner.OwnedWindows.ToArray()) { child.Close(); } try { await moving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { } owner.Close(); }
    }

    private static ScriptContext RemoteContext() => new()
    {
        HasRevisionGrid = true, SelectedRevision = new(new string('a', 40)), CurrentRevision = new(new string('a', 40)), CurrentRemote = "origin",
        SelectedReferences = [new("origin/topic", ScriptReferenceKind.RemoteBranch, "origin"), new("upstream/topic", ScriptReferenceKind.RemoteBranch, "upstream")]
    };
    private static ScriptDefinition PromptScript() => new() { Name = "prompt fixture", Command = "fixture-never-launched", Arguments = "{sRemote}" };
    private static async Task<T> Child<T>(Window owner) where T : Window
    { await Until(owner, () => owner.OwnedWindows.OfType<T>().Any(window => window.IsVisible)); T child = owner.OwnedWindows.OfType<T>().Single(window => window.IsVisible); Pump(child); return child; }
    private static async Task<Window> Message(Window owner)
    { await Until(owner, () => owner.OwnedWindows.Any(window => window.Name == "MessageDialog" && window.IsVisible)); Window message = owner.OwnedWindows.Single(window => window.Name == "MessageDialog" && window.IsVisible); Pump(message); return message; }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Type(Window owner, string value) { TextBox input = Field<TextBox>(owner, "txtUserInput"); input.Focus(); Press(owner, PhysicalKey.A, RawInputModifiers.Control); if (value.Length == 0) { Press(owner, PhysicalKey.Backspace); } else { owner.KeyTextInput(value); } Pump(owner); }
    private static void CancelInput(Window owner)
    { Field<TextBox>(owner, "txtUserInput").Focus(); Press(owner, PhysicalKey.A, RawInputModifiers.Control); Press(owner, PhysicalKey.Escape); if (owner.IsVisible) { Press(owner, PhysicalKey.Escape); } }
    private static void Press(TopLevel root, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { root.KeyPressQwerty(key, modifiers); if (root.IsVisible) { root.KeyReleaseQwerty(key, modifiers); } Pump(root); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); if (root.IsVisible) { root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static Task Until(Window owner, Func<bool> done) => PushProcessTestDriver.UntilAsync(owner, done, "Generic prompt did not settle.");
    private static void RightClick(Window owner, Control target)
    {
        Pump(owner); Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), owner)!.Value;
        Assert.That((owner.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(target), Is.True);
        owner.MouseMove(point); owner.MouseDown(point, MouseButton.Right); owner.MouseUp(point, MouseButton.Right); Pump(owner);
    }
    private sealed class Host : IAsyncDisposable
    {
        internal GitFixture Git { get; }
        internal MainWindow Main { get; }
        private Host(GitFixture git, MainWindow main) { Git = git; Main = main; }
        internal static async Task<Host> CreateAsync(bool remotes = false)
        {
            GitFixture git = await GitFixture.CreateAsync(); MainWindow? main = null;
            try
            {
                await git.ChangeAsync("second version\n", "Prompt host latest");
                if (remotes)
                {
                    foreach (string name in new[] { "origin", "upstream" })
                    { await git.GitAsync("remote", "add", name, git.Repository.Path); await git.GitAsync("update-ref", "refs/remotes/" + name + "/topic", "HEAD"); }
                    new ScriptsPreferencesStore().Save([new ScriptDefinition { Name = "Prompt anchor fixture", Command = "git", Arguments = "config --local promptfixture.selectedremote {sRemote}",
                        AddToRevisionGridContextMenu = true, Enabled = true, HotkeyCommandIdentifier = 9017 }]);
                }
                main = new() { Width = 1100, Height = 750, Position = new PixelPoint(30, 30), WindowStartupLocation = WindowStartupLocation.Manual,
                    ExternalLauncher = _ => throw new AssertionException("Generic prompt routes cannot open an external application.") };
                main.Show(); await main.OpenRepositoryAsync(git.Repository.Path); await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
                string head = (await git.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
                ListBox history = Field<ListBox>(main, "RevisionHistory"); GitCommit selected = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == head);
                history.SelectedItem = selected; history.ScrollIntoView(selected); Pump(main);
                await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); await Until(main, () => !main.IsBusy); Pump(main); return new(git, main);
            }
            catch { main?.Close(); git.Dispose(); throw; }
        }
        internal void OpenScriptMenu()
        {
            ListBox history = Field<ListBox>(Main, "RevisionHistory"); ListBoxItem row = (ListBoxItem)history.ContainerFromIndex(history.SelectedIndex)!;
            RightClick(Main, row); ContextMenu menu = history.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
            MenuItem script = menu.Items.OfType<MenuItem>().Single(item => item.Name == "UserScript_9017"); PushProcessTestDriver.Pointer(script);
        }
        internal async Task OpenMoveMenuAsync()
        {
            TabControl tabs = Field<TabControl>(Main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            FileStatusListView view = Field<FileStatusListView>(Main, "RevisionFileStatusList"); await Until(Main, () => view.Entries.Any(entry => entry.Path == "file.txt"));
            object tag = view.Entries.First(entry => entry.Path == "file.txt").Tag; view.SelectTags([tag]); Pump(Main); await Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            TreeViewItem row = view.FindItemByTag(tag)!; Control header = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header"
                && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
            RightClick(Main, header); ContextMenu menu = view.Tree.ContextMenu!; await Until(Main, () => menu.IsOpen);
            MenuItem move = menu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiMove"); Assert.That(move.IsEnabled, Is.True); PushProcessTestDriver.Pointer(move);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                for (int i = 0; Main.IsVisible && i < 200; i++)
                {
                    foreach (Window child in PushProcessTestDriver.Windows(Main).Reverse().ToArray())
                    {
                        Button? ok = child.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MessageDialogAccept");
                        if (ok is not null) { PushProcessTestDriver.Pointer(ok); } else { child.Close(); }
                    }
                    Main.Close(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10);
                }
                Assert.That(Main.IsVisible, Is.False); await Main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { Git.Dispose(); }
        }
    }
}
