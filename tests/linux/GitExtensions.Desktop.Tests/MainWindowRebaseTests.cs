using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class MainWindowRebaseTests
{
    [AvaloniaTest]
    public async Task BrowseUsesVisibleHistoryOrderForTheRangeAndKeepsTheCheckedOutBranchAsTo()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window;
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string head = await repository.ResolveRevisionAsync("HEAD");
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit[] rows = history.Items.OfType<GitCommit>().ToArray();
        history.SelectedItems!.Clear(); history.SelectedItem = rows[^1]; await Idle(main);
        history.SelectedItems.Add(rows[0]); await Idle(main);
        Invoke(Command(main, "Rebase…"));
        RebaseDialog dialog = await Rebase(main);
        GitRebaseOptions options = dialog.ReadOptions();
        Assert.Multiple(() =>
        {
            Assert.That(options.OnTo, Is.EqualTo(rows[0].Hash));
            Assert.That(options.From, Is.EqualTo(rows[^1].Hash));
            Assert.That(options.BranchName, Is.EqualTo("main"), "Passing literal HEAD would detach a range rebase instead of moving the original branch.");
            Assert.That(options.Interactive, Is.False);
            Assert.That(dialog.HasRepositoryChanges, Is.False, "Browse opens the original options window without starting Git.");
        });
        dialog.Close(); await Idle(main);
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("main"));
    }

    [AvaloniaTest]
    public async Task ContextRebaseAvailabilityFollowsSelectionCountAndBrowseWithThreeSelectionsHasNoRange()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; ListBox history = Field<ListBox>(main, "RevisionHistory");
        GitCommit[] rows = history.Items.OfType<GitCommit>().ToArray();
        MenuItem immediate = Context(main, "HistoryRebaseImmediate"), interactive = Context(main, "HistoryRebaseInteractive"), advanced = Context(main, "HistoryRebaseAdvanced");
        history.SelectedItems!.Clear(); history.SelectedItem = rows[0]; await Idle(main);
        Assert.That(immediate.IsEnabled && interactive.IsEnabled && advanced.IsEnabled, Is.True);
        history.SelectedItems.Add(rows[1]); await Idle(main);
        Assert.That(immediate.IsEnabled || interactive.IsEnabled, Is.False); Assert.That(advanced.IsEnabled, Is.True);
        history.SelectedItems.Add(rows[2]); await Idle(main);
        Assert.That(immediate.IsEnabled || interactive.IsEnabled || advanced.IsEnabled, Is.False);
        Invoke(Command(main, "Rebase…")); RebaseDialog dialog = await Rebase(main);
        GitRebaseOptions options = dialog.ReadOptions();
        Assert.That(options.BranchName, Is.EqualTo(rows[0].Hash));
        Assert.That(options.OnTo, Is.Null); Assert.That(options.From, Is.Null);
        Assert.That(dialog.HasRepositoryChanges, Is.False); dialog.Close(); await Idle(main);
    }

    [AvaloniaTest]
    public async Task CancellingTheContextConfirmationLeavesGitAndItsSuppressionPreferenceUntouched()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n", createMerge: true);
        MainWindow main = fixture.Window; GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string head = await repository.ResolveRevisionAsync("HEAD");
        Invoke(Context(main, "HistoryRebaseImmediate"));
        await Until(() => main.OwnedWindows.Any(window => window.Name == "ConfirmHistoryRebase"));
        Window prompt = main.OwnedWindows.Single(window => window.Name == "ConfirmHistoryRebase");
        Field<CheckBox>(prompt, "DontConfirmHistoryRebase").IsChecked = true;
        Click(prompt, "CancelHistoryRebase"); await Until(() => !prompt.IsVisible); await Idle(main);
        Assert.That(new RebasePreferencesStore().Load().DontConfirmRebase, Is.False);
        Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
        Assert.That((await repository.GetRebaseStateAsync()).IsActive, Is.False);
        Assert.That(main.OwnedWindows.OfType<RebaseDialog>(), Is.Empty);
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) => items.SelectMany(item => new[] { item }.Concat(Flatten(item.Items.OfType<MenuItem>())));
    private static MenuItem Command(MainWindow window, string label) => Flatten(window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>()).Single(item => item.Header as string == label);
    private static MenuItem Context(MainWindow window, string name) => Flatten(Field<ListBox>(window, "RevisionHistory").ContextMenu!.Items.OfType<MenuItem>()).Single(item => item.Name == name);
    private static void Invoke(MenuItem item) { Assert.That(item.IsEnabled, Is.True); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    private static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Idle(MainWindow window) => Until(() => !window.IsBusy);
    private static async Task<RebaseDialog> Rebase(MainWindow owner)
    { await Until(() => owner.OwnedWindows.OfType<RebaseDialog>().Any(dialog => dialog.State is not null && !dialog.IsBusy)); return owner.OwnedWindows.OfType<RebaseDialog>().Single(); }
    private static async Task Until(Func<bool> predicate)
    { DateTime deadline = DateTime.UtcNow.AddSeconds(15); while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); } Assert.That(predicate(), Is.True, "Rebase routing did not settle."); }
}
