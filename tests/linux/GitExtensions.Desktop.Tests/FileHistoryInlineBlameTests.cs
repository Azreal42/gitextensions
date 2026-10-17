using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FileHistoryInlineBlameTests
{
    [AvaloniaTest]
    public async Task OrdinaryBrowseKeepsTheOriginalContentControlAndDoesNotReadBlame()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileContentView normal = new(new TextBox()); normal.ShowFile("file.txt", "ordinary file\n"u8.ToArray());
        FileHistoryBlameController control = new(normal); Window owner = new() { Content = control, Width = 900, Height = 600 }; owner.Show();
        try
        {
            bool handled = await control.TryLoadAsync(repository, "missing-not-read.txt", new("HEAD"), CancellationToken.None);
            Assert.That(handled, Is.False);
            Assert.That(control.NormalView, Is.SameAs(normal)); Assert.That(normal.IsVisible, Is.True);
            Assert.That(normal.TextView.Text, Is.EqualTo("ordinary file\n")); Assert.That(control.View.IsVisible, Is.False);
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    [TestCase(GitCompareRevisionKind.Commit)]
    [TestCase(GitCompareRevisionKind.Index)]
    [TestCase(GitCompareRevisionKind.WorkingTree)]
    public async Task TypedEndpointShowsItsActualBytesAndDoesNotStageOrReadHeadInItsPlace(GitCompareRevisionKind kind)
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string hash = await repository.ResolveRevisionAsync("HEAD"), file = Path.Combine(fixture.Repository, "file.txt");
        await File.WriteAllTextAsync(file, "index selected\n"); await repository.StageAsync(["file.txt"]);
        await File.WriteAllTextAsync(file, "working selected\n");
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git/index"));
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 600 }; owner.Show(); control.SetAvailable(true);
        try
        {
            GitCompareRevision source = kind == GitCompareRevisionKind.Commit ? new(hash) : new(Kind: kind);
            Assert.That(await control.TryLoadAsync(repository, "file.txt", source, CancellationToken.None), Is.True);
            Assert.That(control.View.TextView.Text, Is.EqualTo(kind == GitCompareRevisionKind.Commit ? "base\n" : kind == GitCompareRevisionKind.Index ? "index selected\n" : "working selected\n"));
            Assert.That(control.View.Lines, Has.Count.EqualTo(1));
            if (kind == GitCompareRevisionKind.Commit) { Assert.That(control.View.Lines[0].Line.Hash, Is.EqualTo(hash)); }
            else { Assert.That(control.View.Lines[0].Line.Hash.All(character => character == '0'), Is.True); }
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("working selected\n"));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task UnbornAttributionReportsTheNativeErrorAndDoesNotKeepThePreviousCommitsText()
    {
        using Fixture fixture = await Fixture.CreateAsync("old committed text\n", "old committed text\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            await control.TryLoadAsync(repository, "file.txt", new(await repository.ResolveRevisionAsync("HEAD")), CancellationToken.None);
            await repository.RunAsync(["symbolic-ref", "HEAD", "refs/heads/unborn-inline"]);
            await repository.RunAsync(["read-tree", "--empty"]);
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "newborn.txt"), "actual staged unborn bytes\n"); await repository.StageAsync(["newborn.txt"]);
            GitCommandException? error = null;
            try { await control.TryLoadAsync(repository, "newborn.txt", new(Kind: GitCompareRevisionKind.Index), CancellationToken.None); }
            catch (GitCommandException exception) { error = exception; }
            Assert.That(error, Is.Not.Null); Assert.That(error!.Result.StandardError, Does.Contain("HEAD"));
            Assert.That(control.View.TextView.Text, Does.Not.Contain("old committed text")); Assert.That(control.View.Lines, Is.Empty);
            Assert.That(System.Text.Encoding.UTF8.GetString(await repository.GetIndexFileBytesAsync("newborn.txt")), Is.EqualTo("actual staged unborn bytes\n"));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task IndexAndWorkingTreeHaveDifferentDisplayIdentitiesAndKeepTheSharedCodecAndOptions()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 1000, Height = 700 }; owner.Show(); control.SetAvailable(true);
        try
        {
            await control.TryLoadAsync(repository, "file.txt", new(Kind: GitCompareRevisionKind.Index), CancellationToken.None);
            string? index = control.View.TextView.DocumentIdentity;
            await control.TryLoadAsync(repository, "file.txt", new(Kind: GitCompareRevisionKind.WorkingTree), CancellationToken.None);
            Assert.That(control.View.TextView.DocumentIdentity, Is.Not.EqualTo(index));
            Assert.That(control.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "FileHistoryInlineBlameOptions"), Is.True);
            Assert.That(control.View.CurrentFileEncodingName, Is.EqualTo("utf-8"));
            Assert.That(control.View.Preferences.GitOptions, Is.EqualTo(new BlamePreferences().GitOptions));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task OldRenamedBlobKeepsItsLiteralPathCodecAndRequestedLine()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string oldPath = "ancien é[1].txt", newPath = "renamed λ[1].txt";
        byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes("ancien café\nseconde ligne\n");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Repository, oldPath), bytes);
        await repository.StageAsync([oldPath]); await repository.CommitAsync("Legacy old blob");
        string hash = await repository.ResolveRevisionAsync("HEAD");
        await repository.RunAsync(["mv", "--", oldPath, newPath]); await repository.CommitAsync("Rename legacy blob");
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            control.View.TextView.FileEncodingName = "iso-8859-1";
            control.SetPosition(repository, hash, oldPath, 2);
            await control.TryLoadAsync(repository, oldPath, new(hash), CancellationToken.None);
            Assert.That(control.View.TextView.Text, Is.EqualTo("ancien café\nseconde ligne\n"));
            Assert.That(control.View.CurrentFileEncodingName, Is.EqualTo("iso-8859-1"));
            Assert.That(control.View.TextView.FileEncodingName, Is.EqualTo("iso-8859-1"));
            Assert.That(control.View.CurrentFileLine, Is.EqualTo(2));
            Assert.That(control.View.Lines.All(line => line.OriginalPath == oldPath), Is.True);
            Assert.That(control.View.TextView.DocumentIdentity, Is.EqualTo(string.Join('\0', repository.Path, oldPath, hash, "blame")));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task FolderOrGrepClearShowsTheOriginalViewerWithoutRememberingBlameOff()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            GitCompareRevision source = new(await repository.ResolveRevisionAsync("HEAD"));
            await control.TryLoadAsync(repository, "file.txt", source, CancellationToken.None);
            control.Cancel(showNormal: true); normal.ShowFile("summary.txt", "folder summary\n"u8.ToArray());
            Assert.That(normal.IsVisible, Is.True); Assert.That(control.View.IsVisible, Is.False); Assert.That(control.IsBlameActive, Is.True);
            await control.TryLoadAsync(repository, "file.txt", source, CancellationToken.None);
            Assert.That(control.View.IsVisible, Is.True); Assert.That(normal.IsVisible, Is.False); Assert.That(control.View.TextView.Text, Is.EqualTo("base\n"));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task PassiveBlamePublicationLeavesTheRevisionListsVeryFirstDownUnrepaired()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        ListBox history = new() { ItemsSource = new[] { "first", "second" }, Height = 80 };
        Grid layout = new() { RowDefinitions = new RowDefinitions("80,*") };
        layout.Children.Add(history); Grid.SetRow(control, 1); layout.Children.Add(control);
        Window owner = new() { Content = layout, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            owner.UpdateLayout(); await Task.Delay(20); PushProcessTestDriver.Pointer(history.GetRealizedContainers().OfType<Control>().First());
            Assert.That(history.SelectedIndex, Is.Zero);
            await control.TryLoadAsync(repository, "file.txt", new(await repository.ResolveRevisionAsync("HEAD")), CancellationToken.None);
            owner.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); owner.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.That(history.SelectedIndex, Is.EqualTo(1), "No Focus call or corrective pointer is allowed between publication and this first arrow.");
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task AnOldRealGitReadIsCancelledAndCannotPublishOverTheNewFileOrOutliveClose()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        (GitRepository slow, string started) = Helpers.SlowRepository(fixture, "blame");
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            string hash = await repository.ResolveRevisionAsync("HEAD");
            await control.TryLoadAsync(repository, "file.txt", new(hash), CancellationToken.None);
            string previous = control.View.TextView.Text; string? identity = control.View.TextView.DocumentIdentity;
            Task old = control.TryLoadAsync(slow, "file.txt", new(hash), CancellationToken.None);
            await Helpers.Until(() => File.Exists(started)); int pid = int.Parse(await File.ReadAllTextAsync(started));
            Assert.That(control.View.TextView.Text, Is.EqualTo(previous));
            Assert.That(control.View.TextView.DocumentIdentity, Is.EqualTo(identity), "Keep the previous document until the replacement is ready.");
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "other.txt"), "newest actual bytes\n");
            await repository.StageAsync(["other.txt"]); await repository.CommitAsync("Other file");
            await control.TryLoadAsync(repository, "other.txt", new(await repository.ResolveRevisionAsync("HEAD")), CancellationToken.None);
            await old; await control.Completion;
            Assert.That(control.View.TextView.Text, Is.EqualTo("newest actual bytes\n"));
            Assert.That(Stopped(pid), Is.True, "The blocked native read must drain before fixture cleanup.");
            await control.CloseAsync();
            Assert.That(await control.TryLoadAsync(repository, "file.txt", new(hash), CancellationToken.None), Is.False);
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    public async Task ClosingDuringARealBlameReadSealsRequestsAndWaitsForItsActor()
    {
        using Fixture fixture = await Fixture.CreateAsync("base\n", "base\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        (GitRepository slow, string started) = Helpers.SlowRepository(fixture, "blame");
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 900, Height = 650 }; owner.Show(); control.SetAvailable(true);
        try
        {
            Task read = control.TryLoadAsync(slow, "file.txt", new(await repository.ResolveRevisionAsync("HEAD")), CancellationToken.None);
            await Helpers.Until(() => File.Exists(started)); int pid = int.Parse(await File.ReadAllTextAsync(started));
            await control.CloseAsync(); await read;
            Assert.That(control.Completion.IsCompleted, Is.True); Assert.That(Stopped(pid), Is.True);
            Assert.That(await control.TryLoadAsync(repository, "file.txt", new("HEAD"), CancellationToken.None), Is.False);
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    [AvaloniaTest]
    [TestCase("blameRevisionToolStripMenuItem", false, false)]
    [TestCase("showChangesToolStripMenuItem", true, false)]
    [TestCase("blamePreviousRevisionToolStripMenuItem", false, true)]
    public async Task NavigationUsesTheActualAttributionPathAndLineThroughTheSharedMenu(string command, bool changes, bool previous)
    {
        using Fixture fixture = await Fixture.CreateAsync("base\nsecond\n", "base\nsecond\n"); GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        string oldHash = await repository.ResolveRevisionAsync("HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), "changed first\nsecond\n");
        await repository.StageAsync(["file.txt"]); await repository.CommitAsync("Changed first line");
        string hash = await repository.ResolveRevisionAsync("HEAD");
        FileContentView normal = new(new TextBox()); FileHistoryBlameController control = new(normal);
        Window owner = new() { Content = control, Width = 1000, Height = 700 }; owner.Show(); control.SetAvailable(true);
        FileHistoryBlameNavigation? navigation = null; control.NavigationRequested += target => { navigation = target; return Task.CompletedTask; };
        try
        {
            await control.TryLoadAsync(repository, "file.txt", new(hash), CancellationToken.None); owner.UpdateLayout();
            Control margin = control.GetVisualDescendants().OfType<Control>().Single(item => item.Name == "BlameAuthorMargin");
            Point point = margin.TranslatePoint(new Point(80, control.View.TextView.Editor.TextArea.TextView.DefaultLineHeight * .5), owner)!.Value;
            owner.MouseMove(point); owner.MouseDown(point, MouseButton.Right); owner.MouseUp(point, MouseButton.Right);
            ContextMenu menu = margin.ContextMenu!; await Helpers.Until(() => menu.IsOpen);
            PushProcessTestDriver.Pointer(menu.Items.OfType<MenuItem>().Single(item => item.Name == command));
            await Helpers.Until(() => navigation is not null);
            Assert.That(navigation!.Repository, Is.SameAs(repository)); Assert.That(navigation.Hash, Is.EqualTo(previous ? oldHash : hash));
            Assert.That(navigation.Path, Is.EqualTo("file.txt")); Assert.That(navigation.Line, Is.EqualTo(1)); Assert.That(navigation.ShowChanges, Is.EqualTo(changes));
        }
        finally { await control.CloseAsync(); owner.Close(); normal.Dispose(); }
    }

    private static bool Stopped(int pid)
    {
        string path = "/proc/" + pid + "/stat"; if (!File.Exists(path)) { return true; }
        string raw = File.ReadAllText(path); return raw[(raw.LastIndexOf(')') + 2)..].Split(' ')[0] is "Z" or "X" or "x";
    }
}
