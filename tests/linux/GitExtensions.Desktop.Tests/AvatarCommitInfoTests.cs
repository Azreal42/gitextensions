using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvatarCommitInfoTests
{
    [AvaloniaTest]
    public async Task ActualSavedShowCommitInfoAdmitsAndPublishesItsVisibleImageWithoutReloadingOrReplacingTheSelectedCommit()
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create();
        ListBox history = AvatarUiTestDriver.Field<ListBox>(f.Main, "RevisionHistory");
        GitCommit commit = history.Items.OfType<GitCommit>().Single();
        Control row = (Control)(history.ContainerFromItem(commit) ?? throw new AssertionException("The real commit row must be realized."));
        await AvatarUiTestDriver.Click(f.Main, row); await f.Idle();
        await AvatarUiTestDriver.Click(f.Main, AvatarUiTestDriver.Field<TabItem>(f.Main, "CommitInfoTab")); await f.Idle();
        CommitInformationView information = AvatarUiTestDriver.Field<CommitInformationView>(f.Main, "CommitInformation");
        Control avatar = AvatarUiTestDriver.Field<Control>(f.Main, "CommitInformationAvatar");
        Assert.That(information.IsEffectivelyVisible, Is.True); Assert.That(information.Text, Does.Contain(commit.Hash));
        Assert.That(avatar.IsVisible, Is.False); Assert.That(f.Offline.Memory.Count, Is.Zero); Assert.That(f.Offline.Disk.Reads, Is.Zero);
        int reads = 0; TaskCompletionSource published = OfflineAvatars.Signal();
        AvatarImageControl imageAvatar = (AvatarImageControl)avatar;
        imageAvatar.ReadStarted += _ => reads++; imageAvatar.ImageChanged += () => published.TrySetResult();
        using (await AvatarUiTestDriver.Frame(f.Main)) { } Assert.That(reads, Is.Zero);
        object source = history.ItemsSource ?? throw new AssertionException("The commit source must be present.");
        await AvatarUiTestDriver.Click(f.Main, row); Assert.That(history.IsKeyboardFocusWithin, Is.True);
        DesktopSettingsDialog settings = await f.Settings();
        await AvatarUiTestDriver.Click(settings, AvatarUiTestDriver.Field<CheckBox>(settings, "ShowAuthorAvatarInCommitInfo"));
        GitCommandLog.Clear(); await f.Apply(settings); await published.Task.WaitAsync(OfflineAvatars.Limit);
        Assert.That(f.Desktop.Load().ShowAuthorAvatarInCommitInfo, Is.True); Assert.That(f.Service.Snapshot.Preferences.ShowCommitInfo, Is.True);
        Assert.That(avatar.IsEffectivelyVisible, Is.True); Assert.That(reads, Is.EqualTo(1)); Assert.That(f.Offline.Memory.Count, Is.EqualTo(1));
        Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.ContainerFromItem(commit), Is.SameAs(row)); Assert.That(history.SelectedItem, Is.SameAs(commit));
        Assert.That(GitCommandLog.Snapshot().Any(entry => entry.Command == "git log" || entry.Command == "git diff" || entry.Command == "git ls-tree"), Is.False);
        await f.Cancel(settings);
        using WriteableBitmap frame = await AvatarUiTestDriver.Frame(f.Main);
        // User80 is also black in its center. Its upper-left corner is clear,
        // so this sample distinguishes the provider image from the placeholder.
        Point point = avatar.TranslatePoint(new Point(2, 2), f.Main)!.Value;
        Assert.That(AvatarUiTestDriver.Pixel(frame, (int)Math.Round(point.X * f.Main.RenderScaling), (int)Math.Round(point.Y * f.Main.RenderScaling)),
            Is.EqualTo(Colors.Black), "The provider PNG must fill a corner that the User80 placeholder leaves clear.");
        Assert.That(history.IsKeyboardFocusWithin, Is.True); Assert.That(history.SelectedItem, Is.SameAs(commit));
        Assert.That(information.Text, Does.Contain(commit.Hash));
    }
}
