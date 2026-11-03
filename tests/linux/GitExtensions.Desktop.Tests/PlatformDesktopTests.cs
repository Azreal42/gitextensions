using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformDesktopTests
{
    [AvaloniaTest]
    public async Task ARealCommitClickAndArrowKeepTheGridFocusAndTheSameRows()
    {
        using PlatformRepositoryTests.Fixture f = await PlatformRepositoryTests.Fixture.CreateAsync();
        string file = Path.Combine(f.Repository.Path, "file.txt");
        await File.WriteAllTextAsync(file, "base\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("base");
        await File.WriteAllTextAsync(file, "second\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("second");
        string? previousProfile = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string profile = Path.Combine(f.Repository.Path, "..", "ui-profile");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.GetFullPath(profile));
        MainWindow? window = null;
        try
        {
            window = new(); window.Show(); Pump(window); await window.OpenRepositoryAsync(f.Repository.Path);
            await window.HistoryLoadingCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            ListBox grid = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RevisionHistory");
            await Until(() => !window.IsBusy && grid.Items.OfType<GitCommit>().Count() == 2);
            GitCommit[] commits = grid.Items.OfType<GitCommit>().ToArray(); object?[] rows = grid.Items.ToArray();
            ListBoxItem first = (ListBoxItem)grid.ContainerFromItem(commits[0])!;
            PushProcessTestDriver.Pointer(first); await window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); Pump(window);
            Assert.That(grid.SelectedItem, Is.SameAs(commits[0])); Assert.That(grid.IsKeyboardFocusWithin, Is.True);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            await window.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(15)); Pump(window);
            Assert.That(grid.SelectedItem, Is.SameAs(commits[1])); Assert.That(grid.IsKeyboardFocusWithin, Is.True);
            Assert.That(grid.Items.Count, Is.EqualTo(rows.Length));
            for (int index = 0; index < rows.Length; index++) { Assert.That(grid.Items[index], Is.SameAs(rows[index])); }
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    window.Close(); await Until(() => !window.IsVisible && window.WindowWorkCompletion.IsCompleted);
                    await window.WindowWorkCompletion;
                }
            }
            finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousProfile); }
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(predicate(), Is.True);
    }
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
