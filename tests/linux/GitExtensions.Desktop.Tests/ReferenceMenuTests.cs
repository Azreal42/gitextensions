using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ReferenceMenuTests
{
    private static readonly string[] DirectActions = ["Create branch…", "Delete branch…", "Checkout branch…", "Create tag…", "Delete tag…"];

    [AvaloniaTest]
    public async Task CommandsMenuOpensAllFiveNativeReferenceDialogsAndRefreshesTheirRealMutations()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string original = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "later\n");
        await repository.StageAllAsync();
        await repository.CommitAsync("Later revision for menu selection");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == original);
        await Until(() => !main.IsBusy);

        MenuItem commands = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Commands");
        MenuItem referencesMenu = commands.Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Branches and tags…");
        Assert.That(referencesMenu.IsEnabled, Is.True);
        commands.IsSubMenuOpen = true;
        referencesMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        commands.IsSubMenuOpen = false;
        await Until(() => main.OwnedWindows.Any());
        Window workflow = main.OwnedWindows.Single();
        try
        {
            workflow.UpdateLayout();
            ListBox references = workflow.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitReference>().Any());

            CreateBranchDialog branch = await OpenAsync<CreateBranchDialog>(workflow, "Create branch…");
            Assert.That(branch.ReadOptions().Revision, Is.EqualTo(original), "Creating a branch must use the selected history row.");
            Field<TextBox>(branch, "CreateBranchName").Text = "menu-topic";
            Field<CheckBox>(branch, "CheckoutAfterCreate").IsChecked = false;
            ClickNamed(branch, "CreateBranch");
            await ClosedAsync(main, workflow, branch);
            await Until(() => references.Items.OfType<GitReference>().Any(reference => reference.ShortName == "menu-topic"));
            Assert.That(await repository.ResolveRevisionAsync("menu-topic"), Is.EqualTo(original));
            Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(original), "Refresh must preserve the selected historical revision.");

            references.SelectedItem = references.Items.OfType<GitReference>().Single(reference => reference.ShortName == "menu-topic");
            CheckoutBranchDialog checkout = await OpenAsync<CheckoutBranchDialog>(workflow, "Checkout");
            Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/heads/menu-topic"), "Checkout must retain the selected branch.");
            Assert.That(Field<TextBlock>(checkout, "CheckoutSelectionLabel").Text, Is.EqualTo("Select branch"));
            ClickNamed(checkout, "CheckoutBranch");
            await ClosedAsync(main, workflow, checkout);
            Assert.That((await repository.GetStatusAsync()).Branch, Is.EqualTo("menu-topic"));
            Assert.That(references.Items.OfType<GitReference>().Single(reference => reference.ShortName == "menu-topic").IsCurrent, Is.True);
            Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.StartWith("menu-topic"), "The browser status must be refreshed after checkout.");

            CreateTagDialog tag = await OpenAsync<CreateTagDialog>(workflow, "Create tag…");
            Assert.That(tag.ReadOptions().Revision, Is.EqualTo(original), "Creating a tag must use the selected history row.");
            Field<TextBox>(tag, "CreateTagName").Text = "menu-tag";
            ClickNamed(tag, "CreateTag");
            await ClosedAsync(main, workflow, tag);
            Assert.That(references.Items.OfType<GitReference>().Any(reference => reference.Name == "refs/tags/menu-tag"), Is.True);
            Assert.That((references.SelectedItem as GitReference)?.Name, Is.EqualTo("refs/heads/menu-topic"), "An unrelated mutation must retain reference selection.");
            Assert.That(await repository.ResolveRevisionAsync("menu-tag"), Is.EqualTo(original));

            references.SelectedItem = references.Items.OfType<GitReference>().Single(reference => reference.Name == "refs/tags/menu-tag");
            DeleteTagDialog deleteTag = await OpenAsync<DeleteTagDialog>(workflow, "Delete…");
            Assert.That((Field<ComboBox>(deleteTag, "DeleteTagSelection").SelectedItem as GitReference)?.Name, Is.EqualTo("refs/tags/menu-tag"));
            Capture(deleteTag, "classic-delete-tag.png");
            ClickNamed(deleteTag, "CancelDeleteTag");
            await ClosedAsync(main, workflow, deleteTag);
            Assert.That(references.Items.OfType<GitReference>().Any(reference => reference.Name == "refs/tags/menu-tag"), Is.True, "Cancel must leave the tag and its selection intact.");
            Assert.That((references.SelectedItem as GitReference)?.Name, Is.EqualTo("refs/tags/menu-tag"));
            deleteTag = await OpenAsync<DeleteTagDialog>(workflow, "Delete…");
            ClickNamed(deleteTag, "DeleteTag");
            await ClosedAsync(main, workflow, deleteTag);
            Assert.That(references.Items.OfType<GitReference>().Any(reference => reference.Name == "refs/tags/menu-tag"), Is.False);

            references.SelectedItem = references.Items.OfType<GitReference>().Single(reference => reference.Name == "refs/heads/main");
            checkout = await OpenAsync<CheckoutBranchDialog>(workflow, "Checkout");
            ClickNamed(checkout, "CheckoutBranch");
            await ClosedAsync(main, workflow, checkout);
            references.SelectedItem = references.Items.OfType<GitReference>().Single(reference => reference.Name == "refs/heads/menu-topic");
            DeleteBranchDialog deleteBranch = await OpenAsync<DeleteBranchDialog>(workflow, "Delete…");
            Assert.That(deleteBranch.ReadOptions().Names, Is.EqualTo(new[] { "menu-topic" }), "Deleting must retain the selected branch.");
            ClickNamed(deleteBranch, "DeleteBranches");
            await Until(() => deleteBranch.OwnedWindows.Any());
            Window confirmation = deleteBranch.OwnedWindows.Single();
            confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string != "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await ClosedAsync(main, workflow, deleteBranch);
            Assert.Multiple(() =>
            {
                Assert.That(references.Items.OfType<GitReference>().Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
                Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(original));
                Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            });
            Assert.That((await repository.GetReferencesAsync()).Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
        }
        finally { workflow.Close(); }
    }

    [AvaloniaTest]
    public async Task DirectCommandsKeepOriginalIconsAndSelectionsAndRefreshCreationCheckoutAndDeletion()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string selectedHash = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "later\n");
        await repository.StageAllAsync();
        await repository.CommitAsync("Later direct-menu revision");
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        AssertDirectIcons(main);
        HistoryFilterDialog filters = await OpenHistoryFilterAsync(main);
        Field<CheckBox>(filters, "AuthorCheck").IsChecked = true;
        Field<TextBox>(filters, "Author").Text = "Desktop Tester";
        Field<CheckBox>(filters, "MessageCheck").IsChecked = true;
        Field<TextBox>(filters, "Message").Text = "Initial commit";
        Field<CheckBox>(filters, "OnlyFirstParent").IsChecked = true;
        filters.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !main.OwnedWindows.Contains(filters) && !main.IsBusy);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == selectedHash);
        await Until(() => !main.IsBusy);

        CreateBranchDialog create = await OpenDirectAsync<CreateBranchDialog>(main, "Create branch…");
        Assert.That(create.ReadOptions().Revision, Is.EqualTo(selectedHash));
        Field<TextBox>(create, "CreateBranchName").Text = "direct-topic";
        Field<CheckBox>(create, "CheckoutAfterCreate").IsChecked = false;
        ClickNamed(create, "CreateBranch");
        await ClosedAsync(main, main, create);
        Assert.That(NavigationReferences(main).Single(reference => reference.ShortName == "direct-topic").Hash, Is.EqualTo(selectedHash), "Creation must update the sidebar immediately.");
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(selectedHash));

        await SelectNavigationReferenceAsync(main, "refs/heads/direct-topic");
        CheckoutBranchDialog checkout = await OpenDirectAsync<CheckoutBranchDialog>(main, "Checkout branch…");
        Assert.That(checkout.ReadOptions().Revision, Is.EqualTo("refs/heads/direct-topic"));
        ClickNamed(checkout, "CheckoutBranch");
        await ClosedAsync(main, main, checkout);
        Assert.That(NavigationReferences(main).Single(reference => reference.ShortName == "direct-topic").IsCurrent, Is.True);
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.StartWith("direct-topic"));

        CreateTagDialog tag = await OpenDirectAsync<CreateTagDialog>(main, "Create tag…");
        Assert.That(tag.ReadOptions().Revision, Is.EqualTo(selectedHash));
        Field<TextBox>(tag, "CreateTagName").Text = "direct-tag";
        ClickNamed(tag, "CreateTag");
        await ClosedAsync(main, main, tag);
        Assert.That(NavigationReferences(main).Single(reference => reference.Name == "refs/tags/direct-tag").Hash, Is.EqualTo(selectedHash));
        await SelectNavigationReferenceAsync(main, "refs/tags/direct-tag");
        DeleteTagDialog deleteTag = await OpenDirectAsync<DeleteTagDialog>(main, "Delete tag…");
        Assert.That((Field<ComboBox>(deleteTag, "DeleteTagSelection").SelectedItem as GitReference)?.Name, Is.EqualTo("refs/tags/direct-tag"));
        ClickNamed(deleteTag, "DeleteTag");
        await ClosedAsync(main, main, deleteTag);
        Assert.That(NavigationReferences(main).Any(reference => reference.Name == "refs/tags/direct-tag"), Is.False, "Deleting the reference selected in the sidebar must refresh successfully.");
        await AssertHistoryFiltersPreservedAsync(main);

        await SelectNavigationReferenceAsync(main, "refs/heads/main");
        checkout = await OpenDirectAsync<CheckoutBranchDialog>(main, "Checkout branch…");
        ClickNamed(checkout, "CheckoutBranch");
        await ClosedAsync(main, main, checkout);
        await SelectNavigationReferenceAsync(main, "refs/heads/direct-topic");
        DeleteBranchDialog deleteBranch = await OpenDirectAsync<DeleteBranchDialog>(main, "Delete branch…");
        Assert.That(deleteBranch.ReadOptions().Names, Is.EqualTo(new[] { "direct-topic" }));
        ClickNamed(deleteBranch, "DeleteBranches");
        await Until(() => deleteBranch.OwnedWindows.Any());
        deleteBranch.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Content as string != "Cancel")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ClosedAsync(main, main, deleteBranch);
        await AssertHistoryFiltersPreservedAsync(main);
        Assert.Multiple(() =>
        {
            Assert.That(NavigationReferences(main).Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
            Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(selectedHash));
            Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.StartWith("main"));
        });
        Assert.That((await repository.GetReferencesAsync()).Select(reference => reference.Name), Is.EqualTo(new[] { "refs/heads/main" }));
    }

    [AvaloniaTest]
    public async Task DeletingAReferenceDoesNotSilentlyEraseAnExplicitlyTypedHistoryExpression()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await repository.CreateBranchAsync(new GitCreateBranchOptions("manual-filter", Checkout: false));
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(fixture.Repository);
        await SelectNavigationReferenceAsync(main, "refs/heads/manual-filter");
        TextBox expression = main.GetVisualDescendants().OfType<TextBox>().Single(text => text.Watermark == "Revision (default: all refs)");
        expression.Text = "manual-filter";
        DeleteBranchDialog delete = await OpenDirectAsync<DeleteBranchDialog>(main, "Delete branch…");
        Assert.That(delete.ReadOptions().Names, Is.EqualTo(new[] { "manual-filter" }));
        ClickNamed(delete, "DeleteBranches");
        await Until(() => delete.OwnedWindows.Any());
        delete.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(button => button.Content as string != "Cancel")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !delete.IsVisible && !main.OwnedWindows.Contains(delete) && !main.IsBusy);
        Assert.Multiple(() =>
        {
            Assert.That(delete.LastError, Is.Null, delete.LastException?.ToString());
            Assert.That(expression.Text, Is.EqualTo("manual-filter"), "A manual expression must not be silently replaced with all branches.");
            Assert.That(main.LastException, Is.TypeOf<GitCommandException>(), "The now-invalid manual expression must produce an explicit error.");
        });
        Assert.That((await repository.GetReferencesAsync()).Any(reference => reference.ShortName == "manual-filter"), Is.False);
    }

    [AvaloniaTest]
    public async Task BareRepositoryKeepsReferenceMenusAndTheirIconsButDisablesCheckout()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        string barePath = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "references-bare.git");
        GitResult clone = await fixture.Git.RunAsync(fixture.Repository, ["clone", "--bare", fixture.Repository, barePath]);
        Assert.That(clone.Success, Is.True, clone.StandardError);
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(barePath);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        AssertDirectIcons(main);
        foreach (string title in DirectActions)
        {
            Assert.That(Command(main, title).IsEnabled, Is.EqualTo(title != "Checkout branch…"), "Bare repository action: " + title);
        }
        CreateBranchDialog branch = await OpenDirectAsync<CreateBranchDialog>(main, "Create branch…");
        Assert.That(branch.ReadOptions().Checkout, Is.False);
        Assert.That(Field<CheckBox>(branch, "CheckoutAfterCreate").IsEnabled, Is.False);
        ClickNamed(branch, "CancelCreateBranch");
        await ClosedAsync(main, main, branch);
        Assert.That(Command(main, "Checkout branch…").IsEnabled, Is.False, "Closing another dialog must not re-enable worktree-only actions.");
    }

    private static MenuItem Commands(MainWindow main)
        => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Commands");

    private static MenuItem Command(MainWindow main, string title)
        => Commands(main).Items.OfType<MenuItem>().Single(menu => menu.Header as string == title);

    private static void AssertDirectIcons(MainWindow main)
    {
        foreach (string title in DirectActions)
        {
            Assert.That(Command(main, title).Icon, Is.TypeOf<Image>(), title + " must keep its original icon.");
            Image icon = (Image)Command(main, title).Icon!;
            Assert.Multiple(() =>
            {
                Assert.That(icon.Source, Is.TypeOf<Avalonia.Media.Imaging.Bitmap>(), title + " icon must load successfully.");
                Assert.That(icon.Width, Is.EqualTo(16));
                Assert.That(icon.Height, Is.EqualTo(16));
            });
        }
    }

    private static async Task<T> OpenDirectAsync<T>(MainWindow main, string title) where T : RepositoryDialog
    {
        MenuItem commands = Commands(main);
        MenuItem action = Command(main, title);
        Assert.That(action.IsEnabled, Is.True, title);
        commands.IsSubMenuOpen = true;
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        commands.IsSubMenuOpen = false;
        await Until(() => main.OwnedWindows.OfType<T>().Any());
        T dialog = main.OwnedWindows.OfType<T>().Single();
        dialog.UpdateLayout();
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        return dialog;
    }

    private static IEnumerable<TreeViewItem> NavigationNodes(MainWindow main)
        => Field<TreeView>(main, "RepositoryNavigation").Items.OfType<TreeViewItem>().SelectMany(group => group.Items.OfType<TreeViewItem>());

    private static IEnumerable<GitReference> NavigationReferences(MainWindow main)
        => NavigationNodes(main).Select(node => node.Tag).OfType<GitReference>();

    private static async Task SelectNavigationReferenceAsync(MainWindow main, string name)
    {
        TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
        navigation.SelectedItem = NavigationNodes(main).Single(node => node.Tag is GitReference reference && reference.Name == name);
        await Until(() => !main.IsBusy);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    private static async Task<HistoryFilterDialog> OpenHistoryFilterAsync(MainWindow main)
    {
        Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Filter"));
        await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any());
        return main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
    }

    private static async Task AssertHistoryFiltersPreservedAsync(MainWindow main)
    {
        HistoryFilterDialog filters = await OpenHistoryFilterAsync(main);
        GitLogQuery query = filters.ReadQuery();
        Assert.Multiple(() =>
        {
            Assert.That(query.All, Is.True);
            Assert.That(query.Revision, Is.Null, "A deleted navigation reference must return to all branches.");
            Assert.That(query.Author, Is.EqualTo("Desktop Tester"));
            Assert.That(query.Message, Is.EqualTo("Initial commit"));
            Assert.That(query.FirstParent, Is.True);
        });
        filters.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !main.OwnedWindows.Contains(filters) && !main.IsBusy);
    }

    private static T Field<T>(Window owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void ClickNamed(Window owner, string name) => Click(Field<Button>(owner, name));
    private static void Click(Button button)
    {
        Assert.That(button.IsEnabled && button.IsEffectivelyVisible, Is.True, button.Name ?? button.Content?.ToString());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task<T> OpenAsync<T>(Window workflow, string content) where T : RepositoryDialog
    {
        Click(workflow.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == content));
        await Until(() => workflow.OwnedWindows.OfType<T>().Any());
        T dialog = workflow.OwnedWindows.OfType<T>().Single();
        dialog.UpdateLayout();
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        return dialog;
    }

    private static async Task ClosedAsync(MainWindow main, Window workflow, RepositoryDialog dialog)
    {
        await Until(() => !dialog.IsVisible && !workflow.OwnedWindows.Contains(dialog) && !main.IsBusy);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    private static async Task Until(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!predicate()) { await Task.Delay(20, timeout.Token); }
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        Assert.That(Field<CheckBox>(window, "DeleteRemoteTag").Bounds.Height, Is.EqualTo(24));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name);
        frame!.Save(path);
        TestContext.AddTestAttachment(path, "Native delete-tag dialog opened from the real Commands menu");
    }
}
