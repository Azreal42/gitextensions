using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Avatars;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class AvatarSettingsConsumerTests
{
    [AvaloniaTest]
    public async Task ActualMainCancelAndAFailedDesktopWriteNeverPublishTheAvatarDraft()
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create();
        byte[] before = File.ReadAllBytes(f.Desktop.FilePath); AvatarPolicySnapshot policy = f.Service.Snapshot;
        DesktopSettingsDialog canceled = await f.Settings(); await f.EditAvatarDraft(canceled); await f.Cancel(canceled);
        Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(before)); Assert.That(f.Service.Snapshot, Is.EqualTo(policy)); Assert.That(f.Offline.Disk.Clears, Is.Zero);
        DesktopSettingsDialog failed = await f.Settings(); await f.EditAvatarDraft(failed);
        File.Delete(f.Desktop.FilePath); Directory.CreateDirectory(f.Desktop.FilePath);
        try
        {
            await f.Apply(failed, expectFailure: true); Assert.That(failed.IsVisible, Is.True);
            Assert.That(f.Service.Snapshot, Is.EqualTo(policy)); Assert.That(f.Offline.Disk.Clears, Is.Zero);
            Assert.That(AvatarUiTestDriver.Field<AppearanceSettingsPage>(failed, "AppearanceSettingsPage").Draft.AvatarProvider, Is.EqualTo(AvatarProvider.Custom));
        }
        finally { Directory.Delete(f.Desktop.FilePath); File.WriteAllBytes(f.Desktop.FilePath, before); }
        await f.Cancel(failed); Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(before));
        DesktopSettingsDialog reopened = await f.Settings();
        Assert.That(AvatarUiTestDriver.Field<AppearanceSettingsPage>(reopened, "AppearanceSettingsPage").Draft.AvatarProvider, Is.EqualTo(AvatarProvider.None));
        await f.Cancel(reopened);
    }

    [AvaloniaTest]
    public async Task ActualClearUsesTheCommittedPolicyWhileCancelDiscardsTheUnappliedProviderTemplateAndDays()
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create(); DesktopSettingsDialog settings = await f.Settings();
        byte[] before = File.ReadAllBytes(f.Desktop.FilePath); AvatarPolicySnapshot policy = f.Service.Snapshot;
        await f.EditAvatarDraft(settings); await AvatarUiTestDriver.Click(settings, AvatarUiTestDriver.Field<Button>(settings, "ClearImageCache"));
        AvatarPolicySnapshot cleared = await f.Service.WaitForReadyAsync().WaitAsync(OfflineAvatars.Limit);
        Assert.That(cleared.Epoch, Is.EqualTo(policy.Epoch + 1)); Assert.That(cleared.Preferences, Is.EqualTo(policy.Preferences));
        Assert.That(cleared.CapturedDiskCacheDays, Is.EqualTo(13)); Assert.That(f.Offline.Disk.Clears, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(before));
        Assert.That(AvatarUiTestDriver.Field<AppearanceSettingsPage>(settings, "AppearanceSettingsPage").Draft.AvatarProvider, Is.EqualTo(AvatarProvider.Custom));
        await f.Cancel(settings); Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(before));
        Assert.That(f.Service.Snapshot.Epoch, Is.EqualTo(cleared.Epoch)); Assert.That(f.Service.Snapshot.Preferences, Is.EqualTo(policy.Preferences));
    }

    [AvaloniaTest]
    public async Task ClosingActualSettingsDuringAClearDoesNotAbandonTheMutationOrCancelAnotherMappedOwnersReadyWait()
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create(); TaskCompletionSource entered = OfflineAvatars.Signal(), release = OfflineAvatars.Signal();
        f.Offline.Disk.Clear = async _ => { entered.TrySetResult(); await release.Task; };
        Window other = new() { Name = "OtherAvatarReader", Width = 360, Height = 220, Content = new TextBlock { Text = "Independent owner" } };
        f.Application.BindWindow(other); other.Show(); using (await AvatarUiTestDriver.Frame(other)) { }
        AvatarOwnerScope reader = AvatarWindowLifetime.For(other).Scope ?? throw new AssertionException("The other real mapped owner must be admitted.");
        try
        {
            DesktopSettingsDialog settings = await f.Settings(); long epoch = f.Service.Snapshot.Epoch;
            await AvatarUiTestDriver.Click(settings, AvatarUiTestDriver.Field<Button>(settings, "ClearImageCache")); await entered.Task.WaitAsync(OfflineAvatars.Limit);
            Task<AvatarReadResult> pending = reader.ReadAsync(OfflineAvatars.Request("other-reader@example.invalid"));
            Assert.That(pending.IsCompleted, Is.False); Assert.That(f.Service.Snapshot.IsReady, Is.False);
            await f.Cancel(settings);
            Assert.That(reader.IsActive, Is.True); Assert.That(pending.IsCompleted, Is.False); Assert.That(f.Service.Snapshot.IsReady, Is.False);
            release.TrySetResult(); AvatarReadResult result = await pending.WaitAsync(OfflineAvatars.Limit);
            Assert.That(result.Outcome, Is.EqualTo(AvatarReadOutcome.Ready)); Assert.That(result.Epoch, Is.EqualTo(epoch + 1));
            Assert.That(f.Service.Snapshot.IsReady, Is.True); Assert.That(f.Offline.Disk.Clears, Is.EqualTo(1));
            Assert.That(f.Main.IsVisible, Is.True);
        }
        finally { release.TrySetResult(); await f.Service.WaitForReadyAsync().WaitAsync(OfflineAvatars.Limit); await AvatarUiTestDriver.Close(other); }
    }

    [AvaloniaTest]
    public async Task ApplyPublishesTheActualSavedAvatarPolicyAndCancelDiscardsOnlyTheFollowingDraft()
    {
        await using AvatarSettingsMainFixture f = await AvatarSettingsMainFixture.Create(); DesktopSettingsDialog settings = await f.Settings();
        await f.EditAvatarDraft(settings); await f.Apply(settings);
        DesktopPreferences applied = f.Desktop.Load(); AvatarPolicySnapshot policy = await f.Service.WaitForReadyAsync().WaitAsync(OfflineAvatars.Limit);
        Assert.That(applied.AvatarProvider, Is.EqualTo(AvatarProvider.Custom)); Assert.That(applied.CustomAvatarTemplate, Is.EqualTo(AvatarSettingsMainFixture.DraftTemplate));
        Assert.That(applied.AvatarImageCacheDays, Is.EqualTo(31)); Assert.That(policy.Preferences.Provider, Is.EqualTo(AvatarProvider.Custom));
        Assert.That(policy.Preferences.CustomTemplate, Is.EqualTo(applied.CustomAvatarTemplate)); Assert.That(policy.Preferences.CacheDays, Is.EqualTo(31));
        byte[] bytes = File.ReadAllBytes(f.Desktop.FilePath);
        await AvatarUiTestDriver.Choose(settings, AvatarUiTestDriver.Field<ComboBox>(settings, "AvatarProvider"), AvatarProvider.None);
        await f.Cancel(settings); Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(bytes)); Assert.That(f.Service.Snapshot, Is.EqualTo(policy));
        DesktopSettingsDialog reopened = await f.Settings(); AppearanceSettingsValues draft = AvatarUiTestDriver.Field<AppearanceSettingsPage>(reopened, "AppearanceSettingsPage").Draft;
        Assert.That(draft.AvatarProvider, Is.EqualTo(AvatarProvider.Custom)); Assert.That(draft.CustomAvatarTemplate, Is.EqualTo(applied.CustomAvatarTemplate));
        Assert.That(draft.AvatarImageCacheDays, Is.EqualTo(31)); await f.Cancel(reopened);
    }
}

internal sealed class AvatarSettingsMainFixture : IAsyncDisposable
{
    internal const string DraftTemplate = "https://offline-draft.invalid/{email};<None>";
    internal GitFixture Git { get; } internal OfflineAvatarFixture Offline { get; } internal AvatarService Service { get; }
    internal AvatarApplicationLifetime Application { get; } internal MainWindow Main { get; } internal PreferencesStore Desktop { get; }
    private AvatarSettingsMainFixture(GitFixture git, OfflineAvatarFixture offline, AvatarService service, MainWindow main)
    { Git = git; Offline = offline; Service = service; Application = new(service); Main = main; Desktop = new(); Application.BindWindow(Main); }
    internal static async Task<AvatarSettingsMainFixture> Create()
    {
        GitFixture git = await GitFixture.CreateAsync(); OfflineAvatarFixture offline = new(); AvatarSettingsMainFixture? result = null;
        try
        {
            git.SetEnvironment("XDG_CACHE_HOME", git.Output("private avatar cache")); PreferencesStore store = new();
            store.Save(new() { Language = "English", ShowAuthorAvatarInCommitInfo = false });
            new HistoryViewPreferencesStore().Save(new() { ShowAvatar = false });
            offline.Provider = (_, _, _) => Task.FromResult<AvatarPng?>(OfflineAvatars.Image());
            AvatarService service = offline.Create(AvatarPreferences.From(store.Load())); MainWindow main = new() { Width = 1300, Height = 860 };
            result = new(git, offline, service, main); main.Show(); await main.OpenRepositoryAsync(git.Repository.Path); await result.Idle(); return result;
        }
        catch
        {
            if (result is not null) { await result.DisposeAsync(); } else { offline.Dispose(); git.Dispose(); }
            throw;
        }
    }
    internal async Task Idle()
    {
        await Main.WindowWorkCompletion.WaitAsync(OfflineAvatars.Limit); await Main.RepositoryBranchLoading.WaitAsync(OfflineAvatars.Limit);
        await Main.CommitInformationCompletion.WaitAsync(OfflineAvatars.Limit); using (await AvatarUiTestDriver.Frame(Main)) { }
        Assert.That(Main.IsBusy, Is.False); Assert.That(Main.LastException, Is.Null, Main.LastException?.ToString());
    }
    internal async Task<DesktopSettingsDialog> Settings()
    {
        TaskCompletionSource<DesktopSettingsDialog> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<DesktopSettingsDialog> mapping = [];
        void OnMapped(object? sender, EventArgs args)
        {
            DesktopSettingsDialog window = (DesktopSettingsDialog)sender!;
            window.Opened -= OnMapped; mapping.Remove(window);
            if (ReferenceEquals(window.Owner, Main)) { opened.TrySetResult(window); }
        }
        // The global WindowOpenedEvent precedes Owner assignment. Observe the actual
        // instance Opened event before checking the owner; do not repair focus or poll.
        using IDisposable subscription = Window.WindowOpenedEvent.AddClassHandler<DesktopSettingsDialog>((window, _) =>
        { mapping.Add(window); window.Opened += OnMapped; });
        try
        {
            AvatarUiTestDriver.Press(Main, PhysicalKey.Comma, RawInputModifiers.Control);
            DesktopSettingsDialog dialog = await opened.Task.WaitAsync(OfflineAvatars.Limit);
            SettingsNavigationTestDriver.Select(dialog, "Appearance"); using (await AvatarUiTestDriver.Frame(dialog)) { }
            GitEnvironmentSettingsSession environment = dialog.GitEnvironmentSession ?? throw new AssertionException("The actual Settings host must own its environment scan.");
            await environment.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(environment.IsBusy, Is.False); Assert.That(AvatarUiTestDriver.Field<Button>(dialog, "ApplyDesktopSettings").IsEffectivelyEnabled, Is.True);
            return dialog;
        }
        finally { foreach (DesktopSettingsDialog window in mapping) { window.Opened -= OnMapped; } }
    }
    internal async Task EditAvatarDraft(DesktopSettingsDialog dialog)
    {
        await AvatarUiTestDriver.Choose(dialog, AvatarUiTestDriver.Field<ComboBox>(dialog, "AvatarProvider"), AvatarProvider.Custom);
        await AvatarUiTestDriver.Edit(dialog, AvatarUiTestDriver.Field<TextBox>(dialog, "txtCustomAvatarTemplate"), DraftTemplate);
        NumericUpDown days = AvatarUiTestDriver.Field<NumericUpDown>(dialog, "_NO_TRANSLATE_DaysToCacheImages");
        await AvatarUiTestDriver.Edit(dialog, days.GetVisualDescendants().OfType<TextBox>().Single(), "31");
    }
    internal async Task Apply(DesktopSettingsDialog dialog, bool expectFailure = false)
    {
        TaskCompletionSource completed = OfflineAvatars.Signal(); bool publishing = false;
        void StateChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property != InputElement.IsEnabledProperty) { return; }
            if (!dialog.Navigation.IsEnabled) { publishing = true; }
            else if (publishing) { completed.TrySetResult(); }
        }
        dialog.Navigation.PropertyChanged += StateChanged;
        try
        {
            await AvatarUiTestDriver.Click(dialog, AvatarUiTestDriver.Field<Button>(dialog, "ApplyDesktopSettings"));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            string? notice = AvatarUiTestDriver.Field<TextBlock>(dialog, "SettingsNotice").Text;
            if (expectFailure) { Assert.That(notice, Does.Contain("Some settings may already have been saved")); }
            else { Assert.That(notice, Is.Null.Or.Empty); await Service.WaitForReadyAsync().WaitAsync(OfflineAvatars.Limit); await Idle(); }
            Assert.That(dialog.IsVisible, Is.True);
        }
        finally { dialog.Navigation.PropertyChanged -= StateChanged; }
    }
    internal async Task Cancel(DesktopSettingsDialog dialog)
    {
        TaskCompletionSource closed = OfflineAvatars.Signal(); dialog.Closed += (_, _) => closed.TrySetResult();
        await AvatarUiTestDriver.Click(dialog, AvatarUiTestDriver.Field<Button>(dialog, "CancelDesktopSettings"));
        await closed.Task.WaitAsync(OfflineAvatars.Limit); await Idle();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (Window child in Main.OwnedWindows.ToArray()) { await AvatarUiTestDriver.Close(child); }
            await AvatarUiTestDriver.Close(Main); await Application.ShutdownAsync().WaitAsync(OfflineAvatars.Limit);
        }
        finally { Offline.Dispose(); Git.Dispose(); }
    }
}
