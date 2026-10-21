using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class OpenRepositoryDialogTests
{
    [Test]
    public void SuggestionsFollowWindowsSourceOrderAndOnlyUseTheFallbackWhenNoCandidateExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "open-local-root"), current = Path.Combine(root, "current");
        RepositoryHistoryState history = new() { Recent = [new(root + "/"), new(current), new(current), new(current.ToUpperInvariant())], LastVisitedRepositoryPath = "/fallback" };
        Assert.That(OpenRepositoryDialog.SuggestedDirectories(root, current + "/", history, "/home-user"),
            Is.EqualTo(new[] { root + "/", current, current.ToUpperInvariant() }));
        Assert.That(OpenRepositoryDialog.SuggestedDirectories(null, null, new() { LastVisitedRepositoryPath = "/last-visited" }, "/home-user"),
            Is.EqualTo(new[] { "/last-visited/", "/home-user/" }));
        Assert.That(OpenRepositoryDialog.SuggestedDirectories(" ", null, new(), "/home-user/"), Is.EqualTo(new[] { "/home-user/" }));
        Assert.That(OpenRepositoryDialog.SuggestedDirectories("/configured", null, new(), "/home-user"), Is.EqualTo(new[] { "/configured/" }));
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task CompactOriginalFieldsIconsAndFirstFocusRemainReadableAtTheMinimumWidth(string language, bool dark)
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        OpenRepositoryDialog dialog = new(null, f.Repository, new()) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Task completion = dialog.ShowDialog(f.Window);
        try
        {
            Pump(dialog); await Until(dialog, () => Editor(dialog).IsKeyboardFocusWithin);
            Assert.That(dialog.Title, Is.EqualTo(translations.Translate("FormOpenDirectory", "$this", "Text", "Open local repository")));
            Assert.That(dialog.Width, Is.EqualTo(615)); Assert.That(dialog.MinWidth, Is.EqualTo(450)); Assert.That(dialog.MaxWidth, Is.EqualTo(800));
            Assert.That(dialog.MinHeight, Is.EqualTo(dialog.MaxHeight));
            Assert.That(dialog.CanMinimize, Is.False); Assert.That(dialog.CanMaximize, Is.False);
            Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").IsEditable, Is.True);
            Assert.That(Field<Button>(dialog, "Load").IsDefault, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<Image>().Where(image => image.Source is not null).Select(image => image.Tag),
                Is.EquivalentTo(new[] { "NavigateUp.png", "BrowseFileExplorer.png", "RepoOpen.png" }));
            dialog.Width = dialog.MinWidth; Pump(dialog);
            foreach (Control control in new Control[] { Field<Label>(dialog, "label1"), Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory"),
                Field<Button>(dialog, "folderGoUpButton"), Field<Button>(dialog, "folderBrowserButton"), Field<Button>(dialog, "Load") })
            {
                Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
                Assert.That(new Rect(default, dialog.ClientSize).Contains(bounds), Is.True, control.Name);
                Assert.That(control.Bounds.Width, Is.GreaterThan(0), control.Name);
            }
            Capture(dialog, "open-local-repository-" + language + (dark ? "-dark" : "-light"));
            Press(dialog, PhysicalKey.Escape); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(dialog.CreatedRepository, Is.Null);
        }
        finally { await Close(dialog, completion); }
    }

    [AvaloniaTest]
    [TestCase("root")] [TestCase("descendant")] [TestCase("worktree")] [TestCase("worktree-file")] [TestCase("bare")]
    public async Task ActualDiscoveryReturnsTheNormalizedCandidateWithoutPublishingHistoryOrChangingItsIndex(string kind)
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        string path = f.Repository;
        if (kind == "descendant") { path = Path.Combine(path, "nested λ [literal]"); Directory.CreateDirectory(path); }
        if (kind is "worktree" or "worktree-file")
        {
            path = Path.Combine(Path.GetDirectoryName(path)!, "linked worktree λ");
            GitResult result = await f.Git.RunAsync(f.Repository, ["worktree", "add", "--detach", path]); Assert.That(result.Success, Is.True, result.StandardError);
        }
        if (kind == "bare")
        {
            path = Path.Combine(Path.GetDirectoryName(path)!, "bare.git");
            _ = await GitRepository.CloneAsync(f.Repository, path, new GitCloneOptions(Bare: true));
        }
        string expectedRoot = kind == "descendant" ? f.Repository : path;
        if (kind == "worktree-file") { path = Path.Combine(path, ".git"); Assert.That(File.Exists(path), Is.True); }
        byte[] history = File.ReadAllBytes(new RepositoryHistoryStore().FilePath);
        byte[] index = File.ReadAllBytes(Path.Combine(f.Repository, ".git", "index"));
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        try
        {
            Pump(dialog); Edit(dialog, path); Pointer(Field<Button>(dialog, "Load"));
            await Until(f.Window, () => !dialog.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.ValidationException, Is.Null); Assert.That(dialog.CreatedRepository, Is.Not.Null);
            Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(expectedRoot));
            Assert.That((await dialog.CreatedRepository.GetInfoAsync()).IsBare, Is.EqualTo(kind == "bare"));
            Assert.That(File.ReadAllBytes(new RepositoryHistoryStore().FilePath), Is.EqualTo(history), "The dialog only discovers; Main performs the sole MRU publication.");
            Assert.That(File.ReadAllBytes(Path.Combine(f.Repository, ".git", "index")), Is.EqualTo(index));
        }
        finally { await Close(dialog, completion); }
    }

    [AvaloniaTest]
    public async Task MissingAndNonGitDirectoriesKeepTheDialogAndItsLiteralTextForARealRetry()
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        string nonGit = Path.Combine(Path.GetDirectoryName(f.Repository)!, "not a repository λ"); Directory.CreateDirectory(nonGit);
        byte[] before = File.ReadAllBytes(new RepositoryHistoryStore().FilePath);
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        try
        {
            foreach (string path in new[] { nonGit + "/absent", nonGit })
            {
                Pump(dialog); Edit(dialog, path); Pointer(Field<Button>(dialog, "Load"));
                await Until(dialog, () => dialog.OwnedWindows.Any(window => window.Name == "MessageDialog"));
                Window error = dialog.OwnedWindows.Single(window => window.Name == "MessageDialog");
                Assert.That(error.Owner, Is.SameAs(dialog)); Assert.That(dialog.CreatedRepository, Is.Null);
                Assert.That(dialog.ValidationException, Is.Not.Null); Assert.That(completion.IsCompleted, Is.False);
                Assert.That(Field<TextBlock>(error, "MessageDialogText").Text, Is.EqualTo(dialog.ValidationError));
                Pointer(Field<Button>(error, "MessageDialogAccept")); await Until(dialog, () => !dialog.IsBusy);
                Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").Text, Is.EqualTo(path));
                Assert.That(File.ReadAllBytes(new RepositoryHistoryStore().FilePath), Is.EqualTo(before));
            }
            Edit(dialog, "  " + f.Repository + "  ");
            TestContext.Progress.WriteLine(RetryInputState(dialog, "before-final-Enter"));
            Press(dialog, PhysicalKey.Enter);
            TestContext.Progress.WriteLine(RetryInputState(dialog, "after-final-Enter"));
            try { await Until(f.Window, () => !dialog.IsVisible); }
            catch
            {
                TestContext.Progress.WriteLine(RetryInputState(dialog, "final-Enter-timeout"));
                try { Capture(dialog, "open-retry-enter-timeout-" + Guid.NewGuid().ToString("N")); }
                catch (Exception captureError) { TestContext.Progress.WriteLine("Retry diagnostic capture failed: " + captureError); }
                throw;
            }
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(f.Repository)); Assert.That(dialog.ValidationException, Is.Null);
        }
        finally { await Close(dialog, completion); }
    }

    [AvaloniaTest]
    public async Task EnterAcceptsTheActualFilesystemSuggestionThenASecondEnterOpensTheClosedCombo()
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        byte[] history = File.ReadAllBytes(new RepositoryHistoryStore().FilePath);
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        try
        {
            Pump(dialog); Edit(dialog, f.Repository[..^1]);
            await Until(dialog, () => OpenCompletion(dialog) is not null && Editor(dialog).Text == f.Repository);
            Popup suggestions = OpenCompletion(dialog)!;
            Assert.That(Editor(dialog).SelectedText, Is.EqualTo(f.Repository[^1..]));
            Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").IsDropDownOpen, Is.False);
            Press(dialog, PhysicalKey.Enter);
            Assert.That(suggestions.IsOpen, Is.False);
            Assert.That(dialog.IsVisible, Is.True, "An open filesystem popup owns Enter; its acceptance must not also execute the default Open button.");
            Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(Editor(dialog).Text, Is.EqualTo(f.Repository));
            Assert.That(Editor(dialog).SelectedText, Is.Empty);
            Assert.That(File.ReadAllBytes(new RepositoryHistoryStore().FilePath), Is.EqualTo(history));
            Press(dialog, PhysicalKey.Enter);
            await Until(f.Window, () => !dialog.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(f.Repository)); Assert.That(dialog.ValidationException, Is.Null);
            Assert.That(File.ReadAllBytes(new RepositoryHistoryStore().FilePath), Is.EqualTo(history), "The dialog discovers once and never publishes history.");
        }
        finally { await Close(dialog, completion); }
    }

    [AvaloniaTest]
    public async Task UpUsesTheRealParentAndRestoresTheCaretWhileRootAndInvalidPathsDisableIt()
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        try
        {
            Pump(dialog); Edit(dialog, f.Repository + "/"); Button up = Field<Button>(dialog, "folderGoUpButton");
            Assert.That(up.IsEnabled, Is.True); Pointer(up);
            string expected = Path.GetDirectoryName(f.Repository)! + "/";
            Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").Text, Is.EqualTo(expected));
            Assert.That(Editor(dialog).IsKeyboardFocusWithin, Is.True);
            Assert.That(Editor(dialog).CaretIndex, Is.EqualTo(expected.Length)); Assert.That(Editor(dialog).SelectedText, Is.Empty);
            string root = Path.GetPathRoot(f.Repository)!;
            string rootChild = root + f.Repository[root.Length..].Split(Path.DirectorySeparatorChar)[0];
            Edit(dialog, rootChild); Pointer(up);
            Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").Text, Is.EqualTo(root), "The root separator must not double when navigating from its direct child.");
            Assert.That(up.IsEnabled, Is.False);
            Edit(dialog, "/"); Assert.That(up.IsEnabled, Is.False);
            Edit(dialog, f.Repository + "/missing"); Assert.That(up.IsEnabled, Is.False); Assert.That(dialog.CreatedRepository, Is.Null);
        }
        finally { await Close(dialog, completion); }
    }

    [AvaloniaTest]
    public async Task CloseCancelsAndDrainsAnActualFilesystemResultBeforeTheModalCanReturn()
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        string directory = Path.Combine(Path.GetDirectoryName(f.Repository)!, "completion child λ"); Directory.CreateDirectory(directory);
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Pump(dialog);
            dialog.DirectoryCompletionBeforeApply = (typed, paths) => { Assert.That(paths, Does.Contain(directory)); received.TrySetResult(); return release.Task; };
            Edit(dialog, Path.GetDirectoryName(directory)! + "/completion");
            await Until(dialog, () => received.Task.IsCompleted);
            Task pending = dialog.WindowWorkCompletion; dialog.Close(); Pump(dialog);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(completion.IsCompleted, Is.False); Assert.That(pending.IsCompleted, Is.False);
            release.TrySetResult(); await Until(f.Window, () => !dialog.IsVisible);
            await completion.WaitAsync(TimeSpan.FromSeconds(5)); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.CreatedRepository, Is.Null); Assert.That(f.Window.IsVisible, Is.True);
            Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").Text, Does.Not.EndWith(" child λ"), "The retained filesystem result cannot publish after Close.");
        }
        finally { release.TrySetResult(); await Close(dialog, completion); }
    }

    [AvaloniaTest]
    [TestCase("cancel")] [TestCase("accept")] [TestCase("close")]
    public async Task BrowseKeepsTheRealProviderTaskOwnedAndRejectsItsResultAfterACloseRequest(string result)
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        TaskCompletionSource<string?> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? passedOwner = null; string? initial = null; CancellationToken providerToken = default;
        OpenRepositoryDialog dialog = new(null, null, new(), pickFolder: (owner, path, token) =>
        { passedOwner = owner; initial = path; providerToken = token; return provider.Task; });
        Task completion = dialog.ShowDialog(f.Window);
        try
        {
            Pump(dialog); Edit(dialog, f.Repository); Pointer(Field<Button>(dialog, "folderBrowserButton"));
            Assert.That(passedOwner, Is.SameAs(dialog)); Assert.That(initial, Is.EqualTo(f.Repository));
            Assert.That(dialog.IsBusy, Is.True); Assert.That(dialog.WindowWorkCompletion.IsCompleted, Is.False);
            if (result == "close")
            {
                dialog.Close(); Pump(dialog);
                Assert.That(providerToken.IsCancellationRequested, Is.True);
                Assert.That(provider.Task.IsCompleted, Is.False, "Logical cancellation cannot manufacture a completed native storage-provider Task.");
                Assert.That(dialog.IsVisible, Is.True); Assert.That(completion.IsCompleted, Is.False);
                provider.TrySetResult(f.Repository);
                await Until(f.Window, () => !dialog.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
                await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(dialog.CreatedRepository, Is.Null);
            }
            else if (result == "accept")
            {
                provider.TrySetResult(f.Repository); await Until(f.Window, () => !dialog.IsVisible);
                await completion.WaitAsync(TimeSpan.FromSeconds(5)); await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(dialog.CreatedRepository!.Path, Is.EqualTo(f.Repository));
            }
            else
            {
                provider.TrySetResult(null); await Until(dialog, () => !dialog.IsBusy);
                Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.CreatedRepository, Is.Null);
                Assert.That(Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").Text, Is.EqualTo(f.Repository));
            }
        }
        finally { provider.TrySetResult(null); await Close(dialog, completion); }
    }

    [AvaloniaTest]
    public async Task ClosingDuringAnActualGitDiscoveryCancelsAndDrainsBeforeTheModalCanReturn()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The owned discovery process probe requires Linux procfs."); return; }
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        string temporary = Path.GetDirectoryName(f.Repository)!, helper = Path.Combine(temporary, "slow-discovery-git"), marker = helper + ".entered";
        string quote = "'" + marker.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(helper, "#!/bin/sh\ncase \"$*\" in\n  *--absolute-git-dir*) printf '%s\\n' \"$$\" > " + quote
            + "; while test ! -f " + quote + ".release; do sleep 0.02; done ;;\nesac\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitEnvironmentOptions previous = GitEnvironment.Options;
        OpenRepositoryDialog dialog = new(null, null, new()); Task completion = dialog.ShowDialog(f.Window);
        try
        {
            GitEnvironment.Configure(previous with { Executable = helper });
            Pump(dialog); Edit(dialog, f.Repository); Pointer(Field<Button>(dialog, "Load"));
            await Until(dialog, () => File.Exists(marker)); Assert.That(dialog.IsBusy, Is.True);
            string actor = File.ReadAllText(marker).Trim(); string statPath = "/proc/" + actor + "/stat";
            string actualStat = File.ReadAllText(statPath); string started = actualStat[(actualStat.LastIndexOf(')') + 2)..].Split(' ')[19];
            Task operation = dialog.WindowWorkCompletion; dialog.Close();
            await Until(f.Window, () => !dialog.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await operation.WaitAsync(TimeSpan.FromSeconds(5)); await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(dialog.CreatedRepository, Is.Null);
            if (File.Exists(statPath))
            {
                string remaining = File.ReadAllText(statPath); string[] fields = remaining[(remaining.LastIndexOf(')') + 2)..].Split(' ');
                Assert.That(fields[19], Is.Not.EqualTo(started), "The actual discovery shell PID/start must be retired, including zombies, before the dialog returns.");
            }
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(marker + ".release", "release\n");
                await Close(dialog, completion);
            }
            finally { GitEnvironment.Configure(previous); }
        }
    }

    [AvaloniaTest]
    [TestCase("toolbar")] [TestCase("menu")] [TestCase("hotkey")]
    public async Task EveryBrowseEntryUsesTheSameModalAndPublishesItsExactCandidateOnlyOnce(string route)
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n");
        string target = Path.Combine(Path.GetDirectoryName(f.Repository)!, "other λ repository");
        _ = await GitRepository.CloneAsync(f.Repository, target);
        GitRepository previous = Repository(f.Window);
        if (route == "toolbar") { Pointer(Field<Button>(f.Window, "BrowseOpen")); }
        else if (route == "menu")
        {
            MenuItem start = f.Window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == "Start");
            Pointer(start); Pointer(start.Items.OfType<MenuItem>().Single(item => item.Header as string == "Open repository…"));
        }
        else { Field<TextBox>(f.Window, "RevisionQuickFilter").Focus(); Press(f.Window, PhysicalKey.O, RawInputModifiers.Control); }
        await Until(f.Window, () => f.Window.OwnedWindows.OfType<OpenRepositoryDialog>().Any(window => window.IsVisible));
        OpenRepositoryDialog dialog = f.Window.OwnedWindows.OfType<OpenRepositoryDialog>().Single(window => window.IsVisible);
        try
        {
            Assert.That(dialog.Owner, Is.SameAs(f.Window)); Assert.That(Repository(f.Window), Is.SameAs(previous));
            Pump(dialog); Edit(dialog, target); Pointer(Field<Button>(dialog, "Load"));
            await Until(f.Window, () => !dialog.IsVisible && !f.Window.IsBusy && Repository(f.Window).Path == target);
            await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Repository(f.Window), Is.SameAs(dialog.CreatedRepository), "No second discovery/repository instance or parallel publication path.");
            RepositoryHistoryState history = new RepositoryHistoryStore().Load();
            Assert.That(history.Recent.First().Path, Is.EqualTo(target)); Assert.That(history.Recent.Count(item => item.Path == target), Is.EqualTo(1));
            Assert.That(f.Window.LastException, Is.Null);
        }
        finally { await Close(dialog, Task.CompletedTask); }
    }

    [AvaloniaTest]
    public async Task ARealCancelFromTheSharedEntryPreservesBrowseSelectionAndHistoryAndReturnsTheFirstArrowToTheGrid()
    {
        using Fixture f = await Fixture.CreateAsync("clean\n", "clean\n", createMerge: true);
        ListBox commits = f.Window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Items.OfType<GitCommit>().Any());
        commits.SelectedIndex = 1; commits.Focus(); Pump(f.Window); object selected = commits.SelectedItem!;
        byte[] history = File.ReadAllBytes(new RepositoryHistoryStore().FilePath); GitRepository previous = Repository(f.Window);
        Press(f.Window, PhysicalKey.O, RawInputModifiers.Control);
        await Until(f.Window, () => f.Window.OwnedWindows.OfType<OpenRepositoryDialog>().Any(window => window.IsVisible));
        OpenRepositoryDialog dialog = f.Window.OwnedWindows.OfType<OpenRepositoryDialog>().Single(window => window.IsVisible);
        try
        {
            Pump(dialog); Edit(dialog, f.Repository + "/unsaved"); Press(dialog, PhysicalKey.Escape);
            await Until(f.Window, () => !dialog.IsVisible && commits.IsKeyboardFocusWithin);
            Assert.That(Repository(f.Window), Is.SameAs(previous)); Assert.That(commits.SelectedItem, Is.SameAs(selected));
            Assert.That(File.ReadAllBytes(new RepositoryHistoryStore().FilePath), Is.EqualTo(history));
            Press(f.Window, PhysicalKey.ArrowDown); Assert.That(commits.SelectedIndex, Is.EqualTo(2));
        }
        finally { await Close(dialog, Task.CompletedTask); }
    }

    private static string RetryInputState(OpenRepositoryDialog dialog, string phase)
    {
        static string Identity(IInputElement? element)
        {
            if (element is not Control control) { return element?.GetType().FullName ?? "<none>"; }
            TopLevel? top = TopLevel.GetTopLevel(control);
            return control.GetType().FullName + "/" + control.Name + " top=" + top?.GetType().FullName + "/" + top?.Name
                + " visible=" + control.IsEffectivelyVisible + " enabled=" + control.IsEffectivelyEnabled;
        }
        ComboBox? combo = dialog.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(item => item.Name == "_NO_TRANSLATE_Directory");
        TextBox? editor = combo?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        Window? owner = dialog.Owner as Window;
        Popup[] popups = dialog.GetLogicalDescendants().OfType<Popup>().Concat(dialog.GetVisualDescendants().OfType<Popup>()).Distinct().ToArray();
        string popupState = string.Join(" | ", popups.Select(popup =>
        {
            ListBox? items = popup.Child?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
            string errors = string.Join(" / ", popup.Child?.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsVisible && text.Name?.EndsWith("DirectoryCompletionError", StringComparison.Ordinal) == true)
                .Select(text => text.Text) ?? []);
            return popup.Name + " open=" + popup.IsOpen + " passThrough=" + popup.OverlayDismissEventPassThrough
                + " itemCount=" + items?.ItemCount + " selected=" + items?.SelectedItem + " error=" + errors;
        }));
        return "OPEN-RETRY " + phase + "\ncombo=" + combo?.Text + " editor=" + editor?.Text
            + " editorSelection=" + editor?.SelectedText + " editorCaret=" + editor?.CaretIndex
            + " editorKeyboardFocus=" + editor?.IsKeyboardFocusWithin
            + " comboEnabled=" + combo?.IsEffectivelyEnabled + " editorEnabled=" + editor?.IsEffectivelyEnabled
            + " MRU=" + combo?.IsDropDownOpen + "\nfocus=" + Identity(dialog.FocusManager?.GetFocusedElement())
            + "\nownerFocus=" + Identity(owner?.FocusManager?.GetFocusedElement())
            + "\ndialog visible=" + dialog.IsVisible + " active=" + dialog.IsActive + " enabled=" + dialog.IsEffectivelyEnabled
            + " busy=" + dialog.IsBusy + " work=" + dialog.WindowWorkCompletion.Status
            + " ownerActive=" + owner?.IsActive + " ownerEnabled=" + owner?.IsEffectivelyEnabled
            + "\npopups=" + popupState + "\nvalidation=" + dialog.ValidationError
            + " validationException=" + dialog.ValidationException?.ToString() + " lastError=" + dialog.LastError;
    }

    private static GitRepository Repository(MainWindow owner) => (GitRepository)typeof(MainWindow)
        .GetField("_repository", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Field<T>(Control owner, string name) where T : Control
        => owner.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static TextBox Editor(OpenRepositoryDialog dialog)
        => Field<ComboBox>(dialog, "_NO_TRANSLATE_Directory").GetVisualDescendants().OfType<TextBox>().Single();
    private static Popup? OpenCompletion(OpenRepositoryDialog dialog)
        => dialog.GetLogicalDescendants().OfType<Popup>().Concat(dialog.GetVisualDescendants().OfType<Popup>()).Distinct()
            .FirstOrDefault(popup => popup.Name == "OpenRepositoryDirectoryCompletionPopup" && popup.IsOpen);
    private static void Edit(OpenRepositoryDialog dialog, string text)
    {
        // In this short window the actual suggestion popup can cover the editor.
        // Dismiss it using its real keyboard gesture before attempting a hit.
        if (OpenCompletion(dialog) is { } completion)
        {
            Press(dialog, PhysicalKey.Escape);
            Assert.That(completion.IsOpen, Is.False, "Escape must dismiss the completion before pointing into its covered editor.");
            Assert.That(dialog.IsVisible, Is.True, "The popup owns this Escape; editing must not close the Open dialog.");
        }
        TextBox editor = Editor(dialog); Pointer(editor); Press(dialog, PhysicalKey.A, RawInputModifiers.Control);
        Press(dialog, PhysicalKey.Backspace); if (text.Length > 0) { dialog.KeyTextInput(text); } Pump(dialog);
    }
    private static void Press(Window owner, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { owner.KeyPressQwerty(key, modifiers); if (owner.IsVisible) { owner.KeyReleaseQwerty(key, modifiers); } Pump(owner); }
    private static void Pointer(Control control)
    {
        control.BringIntoView();
        if (TopLevel.GetTopLevel(control) is not OpenRepositoryDialog dialog)
        { PushProcessTestDriver.Pointer(control); return; }
        // Match the strict driver's frame settling. Only this known completion
        // overlay may be the legitimate first recipient of a forwarded click.
        for (int frame = 0; frame < 10; frame++) { Pump(dialog); }
        Point? translated = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), dialog);
        Visual? hit = translated is Point target ? dialog.InputHitTest(target) as Visual : null;
        TextBox editor = Editor(dialog);
        Popup[] open = dialog.GetLogicalDescendants().OfType<Popup>()
            .Concat(dialog.GetVisualDescendants().OfType<Popup>()).Distinct().Where(popup => popup.IsOpen).ToArray();
        Popup? completion = open.Length == 1 && open[0].Name == "OpenRepositoryDirectoryCompletionPopup" ? open[0] : null;
        bool knownOverlay = hit is LightDismissOverlayLayer
            && ReferenceEquals(hit, LightDismissOverlayLayer.GetLightDismissOverlayLayer(dialog));
        bool forwardedTarget = control is Button || ReferenceEquals(control, editor);
        bool configured = open.Length == 1 && completion is not null && completion.IsLightDismissEnabled
            && completion.OverlayDismissEventPassThrough
            && ReferenceEquals(completion.PlacementTarget, editor)
            && ReferenceEquals(completion.OverlayInputPassThroughElement, editor);
        if (!knownOverlay || !forwardedTarget || !configured)
        { PushProcessTestDriver.Pointer(control); return; }

        Popup forwardingPopup = completion!;
        string State(int clicks) => "target=" + control.GetType().Name + "/" + control.Name
            + " visible=" + control.IsEffectivelyVisible + " enabled=" + control.IsEffectivelyEnabled
            + " bounds=" + control.Bounds + " center=" + translated + " client=" + dialog.ClientSize
            + " hit=" + hit?.GetType().Name + " popup=" + forwardingPopup.Name + " open=" + forwardingPopup.IsOpen
            + " passThrough=" + forwardingPopup.OverlayDismissEventPassThrough
            + " editorFocus=" + editor.IsKeyboardFocusWithin + " actualClicks=" + clicks
            + " dialogVisible=" + dialog.IsVisible + " busy=" + dialog.IsBusy + " validation=" + dialog.ValidationError;
        Assert.That(translated is Point center && control.IsEffectivelyVisible && control.IsEffectivelyEnabled
            && control.Bounds.Width > 0 && control.Bounds.Height > 0 && new Rect(default, dialog.ClientSize).Contains(center),
            Is.True, "The forwarded target must retain the strict visibility/enabled/bounds guards. " + State(0));
        Button? button = control as Button; int clicked = 0;
        void ObservedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => clicked++;
        if (button is not null) { button.Click += ObservedClick; }
        try
        {
            Point point = translated!.Value;
            // Do not dismiss by API, synthesize Click, or retry: Avalonia must
            // deliver this one real physical click through its configured overlay.
            dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
            Pump(dialog);
            Assert.That(button is not null ? clicked == 1 : editor.IsKeyboardFocusWithin, Is.True,
                "One completion-overlay click must reach its actual button/editor. " + State(clicked));
        }
        catch
        {
            TestContext.Progress.WriteLine("Completion overlay forwarding failed: " + State(clicked));
            try { Capture(dialog, "open-completion-forwarding-failure-" + Guid.NewGuid().ToString("N")); }
            catch (Exception captureError) { TestContext.Progress.WriteLine("Forwarding diagnostic capture failed: " + captureError); }
            throw;
        }
        finally { if (button is not null) { button.Click -= ObservedClick; } }
    }
    private static void Pump(Window owner)
    { Dispatcher.UIThread.RunJobs(); if (owner.IsVisible) { owner.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Window owner, Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Pump(owner); await Task.Delay(10); }
        Pump(owner); Assert.That(condition(), Is.True, "The actual Open local repository workflow did not settle.");
    }
    private static async Task Close(OpenRepositoryDialog dialog, Task completion)
    {
        foreach (Window child in dialog.OwnedWindows.ToArray()) { child.Close(); }
        if (dialog.IsVisible) { dialog.Close(); }
        await Until(dialog, () => !dialog.IsVisible); await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await dialog.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static void Capture(Window owner, string name)
    {
        Pump(owner); using var frame = owner.CaptureRenderedFrame() ?? throw new AssertionException("No Open local repository frame.");
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
    }
}
