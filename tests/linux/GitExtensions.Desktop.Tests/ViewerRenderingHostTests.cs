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

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ViewerRenderingHostTests
{
    [AvaloniaTest]
    public async Task FocusedXReloadsDifftasticWithTheOriginalSyntaxOptionWithoutChangingGitBytes()
    {
        string? originalOverride = Environment.GetEnvironmentVariable("DFT_SYNTAX_HIGHLIGHT");
        Environment.SetEnvironmentVariable("DFT_SYNTAX_HIGHLIGHT", null);
        try
        {
            using Fixture fixture = await Fixture.CreateAsync("before λ\n", "after λ\n");
            MainWindow main = fixture.Window;
            string parent = Path.GetDirectoryName(fixture.Repository)!;
            string log = Path.Combine(parent, "controlled-syntax.log"), tool = Path.Combine(parent, "controlled-syntax-tool");
            static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            await File.WriteAllTextAsync(tool, "#!/bin/sh\nprintf '%s\\n' \"$DFT_SYNTAX_HIGHLIGHT\" >> " + Quote(log)
                + "\nprintf 'file.txt --- Text\\n\\033[31m1 before λ\\033[m\\n\\033[32m1 after λ\\033[m\\n'\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            GitResult configuration = await fixture.Git.RunAsync(fixture.Repository, ["config", "difftool.difftastic.cmd", Quote(tool) + " \"$LOCAL\" \"$REMOTE\""]);
            Assert.That(configuration.Success, Is.True, configuration.StandardError);
            TabControl tabs = Find<TabControl>(main, "DetailsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab"); main.UpdateLayout();
            FileStatusTestHelpers.Select(Find<TreeView>(main, "WorkingFiles"), "file.txt");
            DiffView view = Find<DiffView>(main, "WorktreeDiff");
            await Until(() => !main.IsBusy && view.CanonicalText.Contains("+after λ", StringComparison.Ordinal));
            await view.ConfigureRepositoryAsync(await GitRepository.OpenAsync(fixture.Repository));
            view.RenderingMenu.SetOptions(new(ShowSyntaxHighlightingInDiff: true));
            byte[] index = await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git", "index"));
            byte[] config = await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git", "config"));
            byte[] worktree = await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, "file.txt"));
            string patch = view.CanonicalText;
            view.Editor.TextArea.Focus(); Press(main, PhysicalKey.T, RawInputModifiers.Control);
            await Until(() => !main.IsBusy && view.EffectiveMode == DiffViewMode.Difftastic && File.Exists(log));
            Assert.That((await File.ReadAllLinesAsync(log)).Last(), Is.EqualTo("on"));
            int selection = view.Text.IndexOf("after λ", StringComparison.Ordinal); Assert.That(selection, Is.GreaterThanOrEqualTo(0));
            view.Select(selection, "after λ".Length); view.Editor.TextArea.Focus();
            GitDiffDocument previous = view.Document!;
            Press(main, PhysicalKey.X, RawInputModifiers.None);
            await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous));
            Assert.That((await File.ReadAllLinesAsync(log)).Last(), Is.EqualTo("off"));
            Assert.That(view.DisplayOptions.SyntaxHighlighting, Is.False);
            Assert.That(view.Text.Substring(view.SelectionStart, view.SelectionLength), Is.EqualTo("after λ"));
            Assert.That(view.CanonicalText, Is.EqualTo(patch));
            TreeView files = Find<TreeView>(main, "WorkingFiles");
            TreeViewItem fileRow = FileStatusTestHelpers.Rows(files).Single(item => item.Tag is GitChange change && change.Path == "file.txt");
            Assert.That(fileRow.Focus(), Is.True);
            Assert.That(files.IsKeyboardFocusWithin, Is.True); Press(main, PhysicalKey.X, RawInputModifiers.None);
            Assert.That(view.RenderingMenu.Options.ShowSyntaxHighlightingInDiff, Is.False, "X outside the reader must not change the original viewer setting.");
            previous = view.Document!; view.Editor.TextArea.Focus(); Press(main, PhysicalKey.X, RawInputModifiers.None);
            await Until(() => !main.IsBusy && !ReferenceEquals(view.Document, previous));
            Assert.That((await File.ReadAllLinesAsync(log)).Last(), Is.EqualTo("on"));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git", "index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, ".git", "config")), Is.EqualTo(config));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo(worktree));
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); Assert.That(main.OwnedWindows, Is.Empty);
        }
        finally { Environment.SetEnvironmentVariable("DFT_SYNTAX_HIGHLIGHT", originalOverride); }
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); }
    private static async Task Until(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!predicate()) { await Task.Delay(20, timeout.Token); }
    }
}
