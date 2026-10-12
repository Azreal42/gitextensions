using System.Globalization;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class SubmoduleRemovalDialogTests
{
    [AvaloniaTest]
    public async Task NoAndClosingTheConfirmationKeepTheExactRegistrationAndEveryModuleByte()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); SubmoduleRemovalDialog dialog = await f.ShowAsync();
        try
        {
            string before = await f.SnapshotAsync(); Click(dialog, "RemoveSubmodule");
            Window question = await QuestionAsync(dialog);
            Assert.That(Field<TextBox>(question, "ConfirmedSubmodulePath").Text, Is.EqualTo(f.Path));
            Assert.That(Field<Button>(question, "ConfirmRemoveSubmodule").IsDefault, Is.True);
            Click(question, "KeepSubmodule"); await Until(() => dialog.OwnedWindows.Count == 0);
            Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
            Click(dialog, "RemoveSubmodule"); question = await QuestionAsync(dialog); question.Close();
            await Until(() => dialog.OwnedWindows.Count == 0); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
            Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").SelectedItem, Is.TypeOf<GitSubmoduleRemovalSelection>());
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task YesRemovesOnlyTheCapturedRowEvenIfListSelectionChangesDuringConfirmation()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); await f.AddSecondAsync();
        GitRepository moduleRepository = new(System.IO.Path.Combine(f.Root.Repository.Path, f.Path));
        await moduleRepository.SetConfigAsync("user.name", "Removal Test"); await moduleRepository.SetConfigAsync("user.email", "removal@example.invalid"); await moduleRepository.SetConfigAsync("commit.gpgsign", "false");
        await File.WriteAllTextAsync(System.IO.Path.Combine(moduleRepository.Path, "file.txt"), "module current commit\n"); await moduleRepository.StageAllAsync(); await moduleRepository.CommitAsync("Module update");
        SubmoduleRemovalDialog dialog = await f.ShowAsync();
        try
        {
            string untracked = System.IO.Path.Combine(f.Root.Repository.Path, f.Path, "untracked λ.bin"); await File.WriteAllBytesAsync(untracked, [0, 255, 128]);
            string internalGit = System.IO.Path.Combine(f.Root.Repository.Path, ".git", "modules", "module");
            string head = await new GitRepository(System.IO.Path.Combine(f.Root.Repository.Path, f.Path)).ResolveRevisionAsync("HEAD");
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleCommit").Text, Is.EqualTo(head));
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleBranch").Text, Is.EqualTo("heads/main"));
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Modified"));
            Assert.That(((GitSubmoduleRemovalSelection)Field<ListBox>(dialog, "RemovalSubmodules").SelectedItem!).IndexHead, Is.Not.EqualTo(head), "Readonly details must never replace the indexed selection identity.");
            // Windows takes the describe suffix from submodule status, including tags in detached checkouts.
            await moduleRepository.CreateTagAsync("module-tag"); await moduleRepository.CheckoutAsync(head, detached: true);
            await dialog.InitializeAsync();
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleBranch").Text, Is.EqualTo("module-tag"));
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleCommit").Text, Is.EqualTo(head));
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Modified"));
            string nativeStatus = (await f.Root.GitAsync("--literal-pathspecs", "submodule", "status", "--", f.Path)).StandardOutput;
            Assert.That(nativeStatus, Does.Contain(head + " " + f.Path + " (module-tag)"), "Readonly description must match the actual native status without parsing newline paths.");
            ListBox modules = Field<ListBox>(dialog, "RemovalSubmodules"); Click(dialog, "RemoveSubmodule"); Window question = await QuestionAsync(dialog);
            modules.SelectedItem = modules.Items.OfType<GitSubmoduleRemovalSelection>().Single(item => item.Name == "other");
            Click(question, "ConfirmRemoveSubmodule"); await Until(() => !dialog.IsBusy && dialog.HasRepositoryChanges);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(modules.Items.OfType<GitSubmoduleRemovalSelection>().Select(item => item.Name), Is.EqualTo(new[] { "other" }));
            Assert.That(await File.ReadAllBytesAsync(untracked), Is.EqualTo(new byte[] { 0, 255, 128 })); Assert.That(Directory.Exists(internalGit), Is.True);
            Assert.That(await new GitRepository(System.IO.Path.Combine(f.Root.Repository.Path, f.Path)).ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(Field<Button>(dialog, "RemoveSubmodule").IsEnabled, Is.False, "The remaining module must require explicit selection.");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task LastRemovalRetainsWorktreeGitmodulesAndCannotResurrectItsRow()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); byte[] before = await File.ReadAllBytesAsync(f.Root.PathInRepository(".gitmodules"));
        SubmoduleRemovalDialog dialog = await f.ShowAsync();
        try
        {
            Click(dialog, "RemoveSubmodule"); Click(await QuestionAsync(dialog), "ConfirmRemoveSubmodule");
            await Until(() => !dialog.IsBusy && dialog.HasRepositoryChanges);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").Items, Is.Empty);
            Assert.That(await File.ReadAllBytesAsync(f.Root.PathInRepository(".gitmodules")), Is.EqualTo(before));
            Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Is.Empty);
            Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleLocalPath").Text, Is.Null.Or.Empty);
            Assert.That(Field<Button>(dialog, "RemoveSubmodule").IsEnabled, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task RegistrationChangedWhileQuestionWasOpenFailsBeforeAnyLiveMutation()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); SubmoduleRemovalDialog dialog = await f.ShowAsync();
        try
        {
            Click(dialog, "RemoveSubmodule"); Window question = await QuestionAsync(dialog);
            await File.AppendAllTextAsync(f.Root.PathInRepository(".gitmodules"), "# changed outside the dialogue\n"); string before = await f.SnapshotAsync();
            Click(question, "ConfirmRemoveSubmodule"); await Until(() => dialog.LastException is not null && !dialog.IsBusy);
            Assert.That(dialog.LastException, Is.TypeOf<InvalidOperationException>()); Assert.That(dialog.LastError, Does.Contain("changed"));
            Assert.That(await f.SnapshotAsync(), Is.EqualTo(before)); Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").Items.Count, Is.EqualTo(1));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task VanishedInitialSelectionNeverFallsBackToAnotherModule()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); GitSubmodule selected = (await f.Root.Repository.GetSubmodulesAsync()).Single();
        await f.AddSecondAsync(); await f.Root.Repository.RemoveSubmoduleAsync((await f.Root.Repository.GetSubmoduleRemovalSelectionsAsync()).Single(item => item.Name == "module"));
        SubmoduleRemovalDialog dialog = new(f.Root.Repository, selected); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null); Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").Items.Count, Is.EqualTo(1));
            Assert.That(Field<ListBox>(dialog, "RemovalSubmodules").SelectedItem, Is.Null); Assert.That(Field<Button>(dialog, "RemoveSubmodule").IsEnabled, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BrowseSubmodulesUsesTheSelectedRegistrationAndRefreshesAfterClosingTheRemovalDialog()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); MainWindow main = new() { Width = 1440, Height = 900, RequestedThemeVariant = ThemeVariant.Dark };
        main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Root.Repository.Path);
            MenuItem menu = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Repository");
            menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Submodules…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.Any(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true)); Window panel = main.OwnedWindows.Single(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true);
            ListBox list = Field<ListBox>(panel, "SubmodulesList"); list.SelectedItem = list.Items.OfType<GitSubmodule>().Single();
            await Until(() => Field<Button>(panel, "RemoveSubmoduleRegistration").IsEnabled); PointerClick(panel, "RemoveSubmoduleRegistration");
            await Until(() => panel.OwnedWindows.OfType<SubmoduleRemovalDialog>().Any()); SubmoduleRemovalDialog dialog = panel.OwnedWindows.OfType<SubmoduleRemovalDialog>().Single();
            await Until(() => !dialog.IsBusy && Field<Button>(dialog, "RemoveSubmodule").IsEnabled);
            Assert.That(dialog.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); PointerClick(dialog, "RemoveSubmodule");
            Window question = await QuestionAsync(dialog); Assert.That(question.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark)); PointerClick(question, "KeepSubmodule");
            await Until(() => dialog.OwnedWindows.Count == 0); Assert.That(await f.Root.Repository.GetSubmodulesAsync(), Has.Count.EqualTo(1));
            PointerClick(dialog, "RemoveSubmodule"); question = await QuestionAsync(dialog); PointerClick(question, "ConfirmRemoveSubmodule");
            await Until(() => !dialog.IsBusy && dialog.HasRepositoryChanges); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            dialog.Close(); await Until(() => !main.IsBusy && list.Items.Count == 0);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); Assert.That(File.Exists(f.Root.PathInRepository(".gitmodules")), Is.True);
            Assert.That(File.Exists(f.Root.PathInRepository(ModuleFilePath("file.txt"))), Is.True);
            Assert.That(Directory.Exists(f.Root.PathInRepository(".git/modules/module")), Is.True);
            Assert.That(Field<Button>(panel, "RemoveSubmoduleRegistration").IsEnabled, Is.False); panel.Close();
        }
        finally { main.Close(); }
    }

    [AvaloniaTest]
    public async Task ClosingBrowseDuringTheConfirmationCannotRemoveAnyRegistration()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); MainWindow main = new() { Width = 1440, Height = 900 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Root.Repository.Path);
            MenuItem menu = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Repository");
            menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Submodules…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.Any(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true)); Window panel = main.OwnedWindows.Single(window => window.Title?.StartsWith("Submodules", StringComparison.Ordinal) == true);
            ListBox list = Field<ListBox>(panel, "SubmodulesList"); list.SelectedItem = list.Items.OfType<GitSubmodule>().Single();
            Click(panel, "RemoveSubmoduleRegistration"); await Until(() => panel.OwnedWindows.OfType<SubmoduleRemovalDialog>().Any());
            SubmoduleRemovalDialog dialog = panel.OwnedWindows.OfType<SubmoduleRemovalDialog>().Single(); await Until(() => !dialog.IsBusy && Field<Button>(dialog, "RemoveSubmodule").IsEnabled);
            string before = await f.SnapshotAsync(); Click(dialog, "RemoveSubmodule"); Window question = await QuestionAsync(dialog); main.Close();
            await Until(() => !dialog.IsVisible && !question.IsVisible && !main.IsBusy); Assert.That(await f.SnapshotAsync(), Is.EqualTo(before));
        }
        finally { if (main.IsVisible) { main.Close(); } }
    }

    private static string ModuleFilePath(string file) => RemovalFixture.ModulePath + "/" + file;

    [AvaloniaTest]
    public async Task FrenchCatalogueAndBothThemesKeepLiteralDetailsAndYesNoVisible()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            using RemovalFixture f = await RemovalFixture.CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                SubmoduleRemovalDialog dialog = await f.ShowAsync(theme);
                try
                {
                    Assert.That(dialog.Title, Is.EqualTo("Sous-modules")); Assert.That(Field<Button>(dialog, "RemoveSubmodule").Content, Is.EqualTo("Supprimer"));
                    Assert.That(Field<TextBlock>(dialog, "RemovalNameHeader").Text, Is.EqualTo("Nom"));
                    Assert.That(Field<TextBlock>(dialog, "RemovalStatusHeader").Text, Is.EqualTo("Statut"));
                    Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleStatus").Text, Is.EqualTo("Up-to-date"), "Original dynamic status strings have no XLF translation.");
                    Assert.That(Field<TextBox>(dialog, "RemovalSubmoduleLocalPath").Text, Is.EqualTo(f.Path));
                    Assert.That(dialog.GetVisualDescendants().OfType<CheckBox>(), Is.Empty, "The original never offers internal-repository deletion.");
                    Capture(dialog, "submodule-removal-" + theme + ".png"); Click(dialog, "RemoveSubmodule"); Window question = await QuestionAsync(dialog);
                    Assert.That(question.ActualThemeVariant, Is.EqualTo(theme)); Assert.That(question.Title, Is.EqualTo("Supprimer"));
                    Assert.That(Field<TextBlock>(question, "SubmoduleRemovalQuestion").Text, Is.Not.EqualTo("Are you sure you want remove the selected submodule?"));
                    Capture(question, "submodule-removal-question-" + theme + ".png"); Click(question, "KeepSubmodule"); await Until(() => dialog.OwnedWindows.Count == 0);
                }
                finally { dialog.Close(); }
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void Click(Window window, string name) => Field<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void PointerClick(Window window, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Button button = Field<Button>(window, name);
        Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static async Task<Window> QuestionAsync(Window owner)
    { await Until(() => owner.OwnedWindows.Any(window => window is SubmoduleRemovalConfirmation)); return owner.OwnedWindows.Single(window => window is SubmoduleRemovalConfirmation); }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 1200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Submodule dialogue did not settle.");
    }
    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? System.IO.Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(directory); frame!.Save(System.IO.Path.Combine(directory, name));
    }

    private sealed class RemovalFixture : IDisposable
    {
        private RemovalFixture(Fixture root, Fixture source) { Root = root; _source = source; }
        private readonly Fixture _source;
        internal Fixture Root { get; }
        internal const string ModulePath = "module[1]* λ\nline";
        internal string Path => ModulePath;
        internal static async Task<RemovalFixture> CreateAsync()
        {
            Fixture root = await Fixture.CreateAsync(); Fixture source = await Fixture.CreateAsync(); RemovalFixture f = new(root, source);
            await root.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--name", "module", "--", source.Repository.Path, ModulePath);
            await root.Repository.StageAllAsync(); await root.Repository.CommitAsync("Module"); return f;
        }
        internal async Task AddSecondAsync()
        { await Root.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--name", "other", "--", _source.Repository.Path, "other"); await Root.Repository.StageAllAsync(); await Root.Repository.CommitAsync("Other module"); }
        internal async Task<SubmoduleRemovalDialog> ShowAsync(ThemeVariant? theme = null)
        {
            SubmoduleRemovalDialog dialog = new(Root.Repository, (await Root.Repository.GetSubmodulesAsync()).Single(item => item.Name == "module")) { RequestedThemeVariant = theme ?? ThemeVariant.Light };
            dialog.Show(); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
        }
        internal async Task<string> SnapshotAsync() => (await Root.GitAsync("ls-files", "--stage", "-z")).StandardOutput
            + (await Root.GitAsync("config", "--local", "--null", "--list")).StandardOutput + Convert.ToHexString(await File.ReadAllBytesAsync(Root.PathInRepository(".gitmodules")))
            + Convert.ToHexString(await File.ReadAllBytesAsync(Root.PathInRepository(ModulePath + "/file.txt")));
        public void Dispose() { _source.Dispose(); Root.Dispose(); }
    }
}
