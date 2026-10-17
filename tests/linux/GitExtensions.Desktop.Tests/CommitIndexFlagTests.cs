using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class CommitIndexFlagTests
{
    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task SettingsRevealAndContextClearsTheRealIndexFlagWithoutStaging(bool skip, bool missing)
    {
        using Fixture f = await Fixture.CreateAsync();
        const string path = "-flag [literal] λ.txt";
        await File.WriteAllTextAsync(f.PathInRepository(path), "indexed bytes\n");
        await f.Repository.StageAsync([path]); await f.Repository.CommitAsync("Flag fixture");
        await File.WriteAllTextAsync(f.PathInRepository(path), "local bytes must survive\n");
        if (missing) { File.Delete(f.PathInRepository(path)); }
        if (skip) { await f.Repository.SetSkipWorktreeAsync([path], true); }
        else { await f.Repository.SetAssumeUnchangedAsync([path], true); }
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        string index = (await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        CommitDialog dialog = new(f.Repository, new CommitPreferencesStore(f.Output("commit-prefs.json"))) { Width = 1200, Height = 850 };
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            FileStatusListView view = Field<FileStatusListView>(dialog, "UnstagedFileStatusList");
            Assert.That(view.Entries.Any(entry => entry.Path == path), Is.False);
            byte[] rawIndex = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
            await Reveal(dialog, view, skip);
            Assert.That(view.Entries.Count(entry => entry.Path == path), Is.EqualTo(1));
            Assert.That(((GitChange)view.Entries.Single(entry => entry.Path == path).Tag).IsModified, Is.False,
                "A flag marker is not an invented modified file.");
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(rawIndex), "Display choices do not mutate the index.");
            Field<TextBox>(dialog, "Message").Text = "draft retained\n";
            await Toggle(dialog, view, path, skip, expectedChecked: true);
            GitIndexFileFlags flags = (await f.Repository.GetIndexFlagsAsync()).Single(file => file.Path == path);
            Assert.That(skip ? flags.IsSkipWorktree : flags.IsAssumeUnchanged, Is.False);
            Assert.That(view.Entries.Single(entry => entry.Path == path).Status, Is.EqualTo(missing ? 'D' : 'M'));
            Assert.That(Field<TextBox>(dialog, "Message").Text, Is.EqualTo("draft retained\n"));
            Assert.That((await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
            if (missing) { Assert.That(File.Exists(f.PathInRepository(path)), Is.False, "Revealing/clearing a flag must not materialize sparse or deleted files."); }
            else { Assert.That(await File.ReadAllTextAsync(f.PathInRepository(path)), Is.EqualTo("local bytes must survive\n")); }
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(dialog.LastError, Is.Null);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task CancelledSettingsCannotReplayAnOldFilterFocusOnANewKeyboardOpening()
    {
        using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.File, "changed\n");
        CommitDialog dialog = new(f.Repository, new CommitPreferencesStore(f.Output("commit-prefs.json")));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            FileStatusListView view = Field<FileStatusListView>(dialog, "UnstagedFileStatusList");
            TextBox editor = Field<ComboBox>(dialog, "UnstagedFilter").GetVisualDescendants().OfType<TextBox>().Single();
            PushProcessTestDriver.Pointer(editor);
            Button settings = view.Toolbar.Children.OfType<Button>().Single(button => button.Name == "UnstagedSettings");
            PushProcessTestDriver.Pointer(settings); Pump(dialog);
            TopLevel popup = TopLevel.GetTopLevel(view.SettingsMenu.Items.OfType<MenuItem>().First())!;
            popup.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); popup.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(dialog);
            Assert.That(view.SettingsMenu.IsOpen, Is.False);
            // Reach the same trigger through genuine keyboard tab navigation after
            // dismissing the earlier pointer opening from the filter editor.
            for (int i = 0; i < 60 && !settings.IsFocused; i++)
            { dialog.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None); Pump(dialog); }
            Assert.That(settings.IsFocused, Is.True);
            dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Pump(dialog);
            Assert.That(view.SettingsMenu.IsOpen, Is.True);
            MenuItem choice = view.SettingsMenu.Items.OfType<MenuItem>().Single(item => item.Name == "tsmiShowSkipWorktreeFiles");
            PushProcessTestDriver.Pointer(choice); await Idle(dialog);
            Assert.That(editor.IsKeyboardFocusWithin, Is.False, "A cancelled earlier opening must not own this later action.");
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ContextCanHideAChangedFileThenRecoverItThroughTheOriginalSettings(bool skip)
    {
        using Fixture f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(f.File, "local change\n");
        const string untracked = "untracked.txt";
        await File.WriteAllTextAsync(f.PathInRepository(untracked), "untracked bytes\n");
        string index = (await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
        string head = await f.Repository.ResolveRevisionAsync("HEAD");
        CommitDialog dialog = new(f.Repository, new CommitPreferencesStore(f.Output("commit-prefs.json")));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            FileStatusListView view = Field<FileStatusListView>(dialog, "UnstagedFileStatusList");
            await Toggle(dialog, view, "file.txt", skip, expectedChecked: false);
            Assert.That(view.Entries.Any(entry => entry.Path == "file.txt"), Is.False);
            Assert.That(view.Entries.Any(entry => entry.Path == untracked), Is.True);
            await Reveal(dialog, view, skip);
            await Toggle(dialog, view, "file.txt", skip, expectedChecked: true);
            Assert.That(view.Entries.Single(entry => entry.Path == "file.txt").Status, Is.EqualTo('M'));
            Assert.That((await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput, Is.EqualTo(index));
            Assert.That(await f.Repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("local change\n"));
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(untracked)), Is.EqualTo("untracked bytes\n"));
            Assert.That((await f.Repository.GetIndexFlagsAsync()).All(file => !file.IsSkipWorktree && !file.IsAssumeUnchanged), Is.True);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task BothFlagsRemainIndependentAndStageAllLeavesTheirBlobsUntouched()
    {
        using Fixture f = await Fixture.CreateAsync();
        const string path = "both.txt";
        await File.WriteAllTextAsync(f.PathInRepository(path), "indexed\n");
        await f.Repository.StageAsync([path]); await f.Repository.CommitAsync("Both flags");
        await File.WriteAllTextAsync(f.PathInRepository(path), "hidden edit\n");
        await f.Repository.SetSkipWorktreeAsync([path], true); await f.Repository.SetAssumeUnchangedAsync([path], true);
        string index = (await f.GitAsync("ls-files", "--stage", "-z", "--", path)).StandardOutput;
        await File.WriteAllTextAsync(f.File, "ordinary staged change\n");
        CommitDialog dialog = new(f.Repository, new CommitPreferencesStore(f.Output("commit-prefs.json")));
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); await Idle(dialog);
            FileStatusListView view = Field<FileStatusListView>(dialog, "UnstagedFileStatusList");
            await Reveal(dialog, view, false); await Reveal(dialog, view, true);
            Assert.That(view.Entries.Count(entry => entry.Path == path), Is.EqualTo(1), "The lower-case s index tag must never create duplicate rows.");
            Field<Button>(dialog, "StageAll").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Idle(dialog);
            Assert.That((await f.GitAsync("ls-files", "--stage", "-z", "--", path)).StandardOutput, Is.EqualTo(index));
            Assert.That((await f.GitAsync("show", ":file.txt")).StandardOutput, Is.EqualTo("ordinary staged change\n"));
            await Toggle(dialog, view, path, true, expectedChecked: true);
            Assert.That((await f.Repository.GetIndexFlagsAsync()).Single(file => file.Path == path), Is.EqualTo(new GitIndexFileFlags(path, true, false)));
            Assert.That(view.Entries.Count(entry => entry.Path == path), Is.EqualTo(1));
            await Toggle(dialog, view, path, false, expectedChecked: true);
            Assert.That((await f.Repository.GetIndexFlagsAsync()).Single(file => file.Path == path), Is.EqualTo(new GitIndexFileFlags(path, false, false)));
            Assert.That(await File.ReadAllTextAsync(f.PathInRepository(path)), Is.EqualTo("hidden edit\n"));
            Assert.That((await f.GitAsync("ls-files", "--stage", "-z", "--", path)).StandardOutput, Is.EqualTo(index));
        }
        finally { dialog.Close(); }
    }

    private static async Task Reveal(CommitDialog dialog, FileStatusListView view, bool skip)
    {
        Pump(dialog);
        Button settings = view.Toolbar.Children.OfType<Button>().Single(button => button.Name == "UnstagedSettings");
        PushProcessTestDriver.Pointer(settings); Pump(dialog);
        Assert.That(view.SettingsMenu.IsOpen, Is.True);
        string name = skip ? "tsmiShowSkipWorktreeFiles" : "tsmiShowAssumeUnchangedFiles";
        MenuItem? item = view.SettingsMenu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == name);
        Assert.That(item, Is.Not.Null, "The original Settings entry is the route back to a hidden indexed file.");
        Assert.That(item!.IsChecked, Is.False); PushProcessTestDriver.Pointer(item); await Idle(dialog);
    }
    private static async Task Toggle(CommitDialog dialog, FileStatusListView view, string path, bool skip, bool expectedChecked)
    {
        TreeViewItem row = view.FindItemByTag(view.Entries.Single(entry => entry.Path == path).Tag)!; row.IsExpanded = true;
        Pump(dialog);
        Control header = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Header" && control.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() == row);
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(point); dialog.MouseDown(point, MouseButton.Right); dialog.MouseUp(point, MouseButton.Right); Pump(dialog);
        ContextMenu menu = view.Tree.ContextMenu!; Assert.That(menu.IsOpen, Is.True);
        MenuItem item = menu.Items.OfType<MenuItem>().Single(item => item.Name == (skip ? "tsmiSkipWorktree" : "tsmiAssumeUnchanged"));
        await Idle(dialog);
        Assert.That(menu.IsOpen, Is.True, "A file's initial diff read must not require closing and reopening its context menu.");
        Assert.That(item.IsChecked, Is.EqualTo(expectedChecked)); Assert.That(item.IsEnabled, Is.True);
        PushProcessTestDriver.Pointer(item); await Idle(dialog);
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Idle(CommitDialog dialog)
    {
        await Task.Delay(20); Stopwatch watch = Stopwatch.StartNew();
        while (dialog.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(dialog.IsBusy, Is.False); Assert.That(dialog.LastError, Is.Null); Pump(dialog);
    }
}
