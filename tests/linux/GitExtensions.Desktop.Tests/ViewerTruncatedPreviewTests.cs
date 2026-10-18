using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class ViewerTruncatedPreviewTests
{
    [AvaloniaTest]
    public async Task MainTruncatedUtf8PreviewOmitsOnlyPendingCharacterAndCodecChangesUseTheUnmodifiedRawCache()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "gitextensions-truncated-preview-" + Guid.NewGuid().ToString("N"));
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        MainWindow? main = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(temporary, "configuration"));
            LegacyViewerEncodingPreferences.Seed(Path.Combine(temporary, "configuration"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            GitRepository repository = await GitRepository.InitAsync(Path.Combine(temporary, "repository"), new GitInitOptions("main"));
            await repository.SetConfigAsync("user.name", "Preview Tester");
            await repository.SetConfigAsync("user.email", "preview@example.invalid");
            await repository.SetConfigAsync("commit.gpgsign", "false");
            await repository.SetConfigAsync("core.autocrlf", "false");
            await repository.SetConfigAsync("core.hooksPath", Path.Combine(temporary, "no-hooks"));
            await repository.SetConfigAsync("i18n.filesencoding", "utf-8");
            byte[] baseline = Encoding.UTF8.GetBytes("tracked baseline\n");
            await File.WriteAllBytesAsync(Path.Combine(repository.Path, "tracked.txt"), baseline);
            await repository.StageAllAsync();
            await repository.CommitAsync("Initial preview fixture");
            const string literal = ":preview[1]* λ.txt";
            const int limit = 2_000_000;
            string beginning = "UTF8 preview header\n" + string.Concat(Enumerable.Range(1, 200).Select(line => $"visible line {line:000}\n"));
            string prefix = beginning + new string('a', limit - 1 - Encoding.UTF8.GetByteCount(beginning));
            byte[] entireFile = Encoding.UTF8.GetBytes(prefix + "é\n");
            Assert.That(entireFile.Length, Is.EqualTo(limit + 2));
            string path = Path.Combine(repository.Path, literal);
            await File.WriteAllBytesAsync(path, entireFile);
            main = new MainWindow { Width = 1440, Height = 900 };
            main.Show();
            await main.OpenRepositoryAsync(repository.Path);
            await Until(() => !main.IsBusy);
            TabControl tabs = Find<TabControl>(main, "DetailsTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "ChangesTab");
            main.UpdateLayout();
            FileStatusTestHelpers.Select(Find<TreeView>(main, "WorkingFiles"), literal);
            DiffView view = Find<DiffView>(main, "WorktreeDiff");
            await Until(() => !main.IsBusy && view.DocumentIdentity?.Contains(literal, StringComparison.Ordinal) == true);
            const string suffix = "\n[Preview limited to 2 MB]";
            Assert.That(view.Text, Is.EqualTo(prefix + suffix), "The byte limit cuts C3/A9, but all prior valid UTF8 remains readable.");
            Assert.That(view.Text, Does.Not.Contain("\ufffd"));
            Assert.That(view.Document, Is.Null);
            Assert.That(view.CanStagePatch, Is.False);
            Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.PlainText));
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
            byte[] index = await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".git", "index"));
            byte[] config = await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".git", "config"));

            const string later = "Later disk bytes must not replace the loaded preview.\n";
            await File.WriteAllTextAsync(path, later);
            ComboBox encoding = DiffViewerInputTestHelpers.VisibleEncoding(main);
            ChooseEncoding(encoding, "iso-8859-1");
            await Until(() => view.Text == prefix + "Ã" + suffix);
            Assert.That(view.FileEncodingName, Is.EqualTo("iso-8859-1"));
            Assert.That(view.Document, Is.Null);
            Assert.That(view.CanStagePatch, Is.False);
            ChooseEncoding(encoding, "utf-8");
            await Until(() => view.Text == prefix + suffix);
            Assert.That(view.Text, Does.Not.Contain("\ufffd"));
            Assert.That(view.Document, Is.Null);
            Assert.That(view.CanStagePatch, Is.False);
            Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(later));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".git", "index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".git", "config")), Is.EqualTo(config));
            Assert.That(await repository.GetIndexFileBytesAsync("tracked.txt"), Is.EqualTo(baseline));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(repository.Path, "tracked.txt")), Is.EqualTo(baseline));
            Assert.That((await repository.GetStatusAsync()).Changes.Single(change => change.Path == literal).IsUntracked, Is.True);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
        finally
        {
            try
            {
                if (main is not null)
                {
                    foreach (Window child in main.OwnedWindows.ToArray()) { child.Close(); }
                    main.Close();
                    await Until(() => !main.IsVisible);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
                CultureInfo.CurrentUICulture = previousCulture;
                if (Directory.Exists(temporary)) { Directory.Delete(temporary, recursive: true); }
            }
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void ChooseEncoding(ComboBox selector, string name)
    {
        int index = GitFileEncodingCodec.AvailableEncodings.ToList().FindIndex(choice => choice.Name == name);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(selector.Items.OfType<DiffViewerEncodingChoice>().Any(choice => choice.Name == name), Is.True,
            "The isolated legacy profile must include the requested encoding in the real selector: " + name);
        Assert.That(selector.IsEnabled && selector.IsEffectivelyVisible, Is.True);
        selector.SelectedIndex = index + 1;
    }
    private static async Task Until(Func<bool> predicate)
    { using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)); while (!predicate()) { await Task.Delay(20, timeout.Token); } }
}
