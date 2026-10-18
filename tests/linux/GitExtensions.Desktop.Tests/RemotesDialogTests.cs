using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RemotesDialogTests
{
    [AvaloniaTest]
    public async Task BothOriginalTabsAndAdvancedFieldsFitTheirMinimumAndNormalWindowsInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.GitAsync("branch", "feature/λ"); await DisabledAsync(f, "sleeping", bare);
        await f.GitAsync("config", "remote.origin.color", "#456789");
        RemotesDialog dialog = await ShowAsync(f.Repository, advanced: true);
        try
        {
            Assert.That(dialog.Name, Is.EqualTo("RemotesDialog"));
            TabControl tabs = Field<TabControl>(dialog, "RemotesTabs");
            Assert.That(tabs.Items.OfType<TabItem>().Select(tab => Header(tab.Header)),
                Is.EqualTo(new[] { "Remote repositories", "Default pull behavior (fetch & merge)" }));
            Assert.That(Remotes(dialog).Select(remote => (remote.Name, remote.Disabled)),
                Is.EquivalentTo(new[] { ("origin", false), ("sleeping", true) }));
            Assert.That(Selected(dialog).Name, Is.EqualTo("origin"));
            Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").IsEditable, Is.True);
            Assert.That(Field<ComboBox>(dialog, "RemoteSettingsPushUrl").IsEditable, Is.True);
            Assert.That(dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name is "CloseRemotes" or "CancelRemotes"), Is.False,
                "The original manager closes with the window chrome or Escape, without an extra action row.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme;
                foreach (Size size in new[] { new Size(dialog.MinWidth, dialog.MinHeight), new Size(1100, 520) })
                {
                    dialog.Width = size.Width; dialog.Height = size.Height;
                    tabs.SelectedIndex = 0; Pump(dialog);
                    AssertInside(dialog, Field<ListBox>(dialog, "RemoteSettingsList"), Field<TextBox>(dialog, "RemoteSettingsName"),
                        Field<ComboBox>(dialog, "RemoteSettingsUrl"), Field<TextBox>(dialog, "RemoteSettingsPrefix"),
                        Field<Button>(dialog, "RemoteSettingsColor"), Field<Button>(dialog, "ResetRemoteSettingsColor"),
                        Field<Button>(dialog, "NewRemote"), Field<Button>(dialog, "DeleteRemote"), Field<Button>(dialog, "ToggleRemoteState"),
                        Field<Button>(dialog, "SaveRemoteSettings"));
                    Assert.That(Field<Button>(dialog, "SaveRemoteSettings").GetVisualDescendants().OfType<Image>().Any(image => image.Tag as string == "Save.png"), Is.True);
                    Capture(dialog, "remotes-repositories-" + theme + "-" + size.Width + "x" + size.Height + ".png");
                    tabs.SelectedIndex = 1; Pump(dialog);
                    Assert.That(Field<TextBox>(dialog, "TrackingLocalBranch").IsReadOnly, Is.True);
                    Assert.That(Field<ComboBox>(dialog, "TrackingMergeBranch").IsEditable, Is.True);
                    AssertInside(dialog, Field<ListBox>(dialog, "BranchTrackingList"), Field<TextBox>(dialog, "TrackingLocalBranch"),
                        Field<ComboBox>(dialog, "TrackingRemote"), Field<ComboBox>(dialog, "TrackingMergeBranch"), Field<Button>(dialog, "SaveBranchTracking"));
                    Assert.That(Field<ListBox>(dialog, "BranchTrackingList").Items.OfType<GitBranchTrackingSnapshot>().Select(branch => branch.LocalBranch),
                        Is.EquivalentTo(new[] { "main", "feature/λ" }));
                    Capture(dialog, "remotes-tracking-" + theme + "-" + size.Width + "x" + size.Height + ".png");
                }
            }
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task EmptyManagerHasANewRemoteBufferAndEscapeDiscardsItWithoutWritingOrPrompting()
    {
        using Fixture f = await Fixture.CreateAsync(); byte[] before = await ConfigBytesAsync(f);
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            Assert.That(Remotes(dialog), Is.Empty);
            Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").Text, Is.Null.Or.Empty);
            Assert.That(Field<Button>(dialog, "SaveRemoteSettings").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "DeleteRemote").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ToggleRemoteState").IsEnabled, Is.False);
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "unsaved";
            Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = f.Output("not-saved.git");
            Assert.That(Field<Button>(dialog, "SaveRemoteSettings").IsEnabled, Is.True);
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            if (dialog.IsVisible) { dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
            await Until(() => !dialog.IsVisible);
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SaveCreatesTheRealRemoteAndFetchesThroughAnOwnedProcessWhileKeepingTheManagerOpen()
    {
        using Fixture f = await Fixture.CreateAsync();
        string bare = f.Output("server with spaces ' λ.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, bare);
        string head = await TextAsync(f, "rev-parse", "HEAD"); new ProcessDialogPreferencesStore().Save(new(false));
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "  upstream  ";
            Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = "  " + bare + "  ";
            Pointer(dialog, "SaveRemoteSettings");
            GitProcessDialog process = await FinishedProcessAsync(dialog);
            Assert.That(dialog.IsVisible && dialog.IsBusy, Is.True);
            Assert.That(process.Result?.Success, Is.True, process.Error?.ToString());
            Assert.That(await TextAsync(f, "config", "--get", "remote.upstream.url"), Is.EqualTo(bare));
            Assert.That(await TextAsync(f, "rev-parse", "refs/remotes/upstream/main"), Is.EqualTo(head));
            Assert.That(process.Owner, Is.SameAs(dialog), "The shared Fetch workflow owns its process from the manager.");
            Pointer(process, "Ok"); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(Selected(dialog).Name, Is.EqualTo("upstream"));
            Assert.That(Selected(dialog).Url, Is.EqualTo(bare));
            Assert.That(await TextAsync(f, "rev-parse", "HEAD"), Is.EqualTo(head));
            Assert.That(await TextAsync(f, "status", "--porcelain"), Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task RenameRetainsAllUrlsRefspecsUnknownSettingsAndTrackingWhileFetchingTheRenamedRemote()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        string second = f.Output("second.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, second);
        await f.GitAsync("config", "--add", "remote.origin.url", second);
        await f.GitAsync("config", "--add", "remote.origin.pushurl", bare); await f.GitAsync("config", "--add", "remote.origin.pushurl", second);
        await f.GitAsync("config", "--add", "remote.origin.fetch", "^refs/heads/private/*");
        await f.GitAsync("config", "--add", "remote.origin.push", "refs/heads/main:refs/heads/published");
        await f.GitAsync("config", "remote.origin.tagOpt", "--no-tags");
        await f.GitAsync("config", "remote.origin.puttykeyfile", "/unchanged/private key.ppk");
        await f.GitAsync("config", "remote.origin.customoption", "literal λ;$()'value");
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        RemotesDialog dialog = await ShowAsync(f.Repository, remote: "origin", advanced: true);
        try
        {
            Assert.That(Selected(dialog).FetchUrls, Is.EqualTo(new[] { bare, second }));
            Assert.That(Selected(dialog).PushUrls, Is.EqualTo(new[] { bare, second }));
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "renamed";
            await SaveAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Remotes(dialog).Select(remote => remote.Name), Is.EqualTo(new[] { "renamed" }));
            Assert.That(await ValuesAsync(f, "remote.renamed.url"), Is.EqualTo(new[] { bare, second }));
            Assert.That(await ValuesAsync(f, "remote.renamed.pushurl"), Is.EqualTo(new[] { bare, second }));
            Assert.That(await ValuesAsync(f, "remote.renamed.fetch"), Is.EqualTo(new[] { "+refs/heads/*:refs/remotes/renamed/*", "^refs/heads/private/*" }));
            Assert.That(await TextAsync(f, "config", "remote.renamed.push"), Is.EqualTo("refs/heads/main:refs/heads/published"));
            Assert.That(await TextAsync(f, "config", "remote.renamed.tagOpt"), Is.EqualTo("--no-tags"));
            Assert.That(await TextAsync(f, "config", "remote.renamed.puttykeyfile"), Is.EqualTo("/unchanged/private key.ppk"));
            Assert.That(await TextAsync(f, "config", "remote.renamed.customoption"), Is.EqualTo("literal λ;$()'value"));
            Assert.That(await TextAsync(f, "config", "branch.main.remote"), Is.EqualTo("renamed"));
            Assert.That(await TextAsync(f, "config", "branch.main.merge"), Is.EqualTo("refs/heads/main"));
            Assert.That(await TextAsync(f, "rev-parse", "refs/remotes/renamed/main"), Is.EqualTo(await TextAsync(f, "rev-parse", "HEAD")));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SeparatePushUrlEqualToFetchOrUncheckedClearsItsOverridesWithoutChangingFetchUrls()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        string other = f.Output("push.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, other);
        await f.GitAsync("config", "--add", "remote.origin.pushurl", other);
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            Assert.That(Field<CheckBox>(dialog, "SeparateRemotePushUrl").IsChecked, Is.True);
            Field<ComboBox>(dialog, "RemoteSettingsPushUrl").Text = bare; await SaveAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await ValuesAsync(f, "remote.origin.pushurl"), Is.Empty);
            Assert.That(await ValuesAsync(f, "remote.origin.url"), Is.EqualTo(new[] { bare }));
            Assert.That(Selected(dialog).HasSeparatePushUrl, Is.False);
            Field<CheckBox>(dialog, "SeparateRemotePushUrl").IsChecked = true;
            Field<ComboBox>(dialog, "RemoteSettingsPushUrl").Text = other; await SaveAsync(dialog);
            Assert.That(await ValuesAsync(f, "remote.origin.pushurl"), Is.EqualTo(new[] { other }));
            Field<CheckBox>(dialog, "SeparateRemotePushUrl").IsChecked = false; await SaveAsync(dialog);
            Assert.That(await ValuesAsync(f, "remote.origin.pushurl"), Is.Empty);
            Assert.That(await TextAsync(f, "config", "remote.origin.url"), Is.EqualTo(bare));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task DisabledRemotesRetainEveryRemoteSettingWhileNativeRemovalClearsRemoteRefsAndUpstreamOnly()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        await f.GitAsync("config", "--add", "remote.origin.url", bare);
        await f.GitAsync("config", "--add", "remote.origin.pushurl", bare);
        await f.GitAsync("config", "remote.origin.puttykeyfile", "/keep private key.ppk");
        await f.GitAsync("config", "remote.origin.custom", "keep λ");
        await File.WriteAllTextAsync(f.File, "local working changes\n");
        string head = await TextAsync(f, "rev-parse", "HEAD"); byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        RemotesDialog dialog = await ShowAsync(f.Repository, advanced: true);
        try
        {
            Assert.That(ToolTip.GetTip(Field<Button>(dialog, "ToggleRemoteState"))?.ToString()?.ToLowerInvariant(), Does.Contain("tracking"),
                "Deactivation must explain the original native removal of remote refs and upstream settings.");
            Assert.That(((Image)Field<Button>(dialog, "ToggleRemoteState").Content!).Tag, Is.EqualTo("EyeClosed.png"));
            Pointer(dialog, "ToggleRemoteState"); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Selected(dialog).Disabled, Is.True);
            Assert.That((await f.Repository.GetRemotesAsync()).Select(remote => remote.Name), Is.Empty);
            Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").IsEffectivelyEnabled, Is.False);
            Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").IsEffectivelyEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "SaveRemoteSettings").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ToggleRemoteState").IsEffectivelyEnabled, Is.True);
            Assert.That(((Image)Field<Button>(dialog, "ToggleRemoteState").Content!).Tag, Is.EqualTo("EyeOpened.png"));
            Assert.That((await new GitRunner().RunAsync(f.Repository.Path, ["show-ref", "--verify", "refs/remotes/origin/main"])).Success, Is.False);
            Assert.That(await ValuesAsync(f, "branch.main.remote"), Is.Empty);
            Assert.That(await ValuesAsync(f, "branch.main.merge"), Is.Empty);
            Assert.That(Selected(dialog).FetchUrls, Is.EqualTo(new[] { bare, bare }));
            Assert.That(Selected(dialog).PushUrls, Is.EqualTo(new[] { bare }));
            Assert.That(Selected(dialog).PuttySshKey, Is.EqualTo("/keep private key.ppk"));
            Pointer(dialog, "ToggleRemoteState"); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Selected(dialog).Disabled, Is.False);
            Assert.That(Selected(dialog).Url, Is.EqualTo(bare));
            Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").IsEffectivelyEnabled, Is.True);
            Assert.That(await ValuesAsync(f, "remote.origin.url"), Is.EqualTo(new[] { bare, bare }));
            Assert.That(await TextAsync(f, "config", "remote.origin.puttykeyfile"), Is.EqualTo("/keep private key.ppk"));
            Assert.That(await TextAsync(f, "config", "remote.origin.custom"), Is.EqualTo("keep λ"));
            Assert.That(await ValuesAsync(f, "branch.main.remote"), Is.Empty);
            Assert.That(await ValuesAsync(f, "branch.main.merge"), Is.Empty);
            Assert.That((await new GitRunner().RunAsync(f.Repository.Path, ["show-ref", "--verify", "refs/remotes/origin/main"])).Success, Is.False,
                "Activation restores configuration only; it does not silently fetch or rebuild upstream settings.");
            Assert.That(await TextAsync(f, "rev-parse", "HEAD"), Is.EqualTo(head));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllTextAsync(f.File), Is.EqualTo("local working changes\n"));
            Assert.That(dialog.HasRepositoryChanges, Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ActiveAndInactiveNameCollisionsKeepTheUserBufferAndNeverOverwriteEitherRemote()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.Repository.AddRemoteAsync("another", bare); await DisabledAsync(f, "sleeping", bare);
        RemotesDialog dialog = await ShowAsync(f.Repository, remote: "origin");
        try
        {
            byte[] before = await ConfigBytesAsync(f);
            foreach (string collision in new[] { "another", "sleeping" })
            {
                Field<TextBox>(dialog, "RemoteSettingsName").Text = collision;
                Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = f.Output("keep my invalid buffer.git");
                Pointer(dialog, "SaveRemoteSettings"); await IdleAsync(dialog);
                Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty);
                Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").Text, Is.EqualTo(collision));
                Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").Text, Is.EqualTo(f.Output("keep my invalid buffer.git")));
                Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
                Assert.That(PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>(), Is.Empty);
                Assert.That(dialog.HasRepositoryChanges, Is.False);
            }
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task DeleteDefaultsToNoAndOnlyExplicitYesRemovesTheSelectedRemote()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            byte[] before = await ConfigBytesAsync(f);
            Pointer(dialog, "DeleteRemote"); Window question = await QuestionAsync(dialog);
            Button yes = CaptionButton(question, "Yes"), no = CaptionButton(question, "No");
            Assert.That(yes.IsDefault, Is.False); Assert.That(no.IsDefault, Is.True);
            question.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            (question.IsVisible ? question : dialog).KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await IdleAsync(dialog);
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
            Assert.That(Remotes(dialog).Select(remote => remote.Name), Is.EqualTo(new[] { "origin" }));
            Pointer(dialog, "DeleteRemote"); question = await QuestionAsync(dialog); PushProcessTestDriver.Pointer(CaptionButton(question, "Yes"));
            await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Remotes(dialog), Is.Empty);
            Assert.That(await ValuesAsync(f, "remote.origin.url"), Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(dialog.IsVisible, Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ExternalConfigurationChangeRefusesStaleSaveAndRetainsTheEditableBufferWithoutFetching()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        RemotesDialog dialog = await ShowAsync(f.Repository, advanced: true);
        try
        {
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "my-rename";
            Field<TextBox>(dialog, "RemoteSettingsPrefix").Text = "my-prefix";
            await f.GitAsync("config", "remote.origin.external-change", "external value"); byte[] external = await ConfigBytesAsync(f);
            Pointer(dialog, "SaveRemoteSettings"); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").Text, Is.EqualTo("my-rename"));
            Assert.That(Field<TextBox>(dialog, "RemoteSettingsPrefix").Text, Does.StartWith("my-prefix"));
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(external));
            Assert.That(await TextAsync(f, "config", "remote.origin.url"), Is.EqualTo(bare));
            Assert.That(PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>(), Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task InheritedRemoteExplainsWhyRenameDeleteAndActivationAreUnavailableWithoutChangingItsSource()
    {
        using Fixture f = await Fixture.CreateAsync();
        string bare = f.Output("inherited.git"); await f.GitAsync("clone", "--bare", f.Repository.Path, bare);
        string include = f.Output("shared.conf");
        await File.WriteAllTextAsync(include, "[remote \"inherited\"]\n\turl = " + GitQuote(bare) + "\n\tfetch = +refs/heads/*:refs/remotes/inherited/*\n");
        await f.GitAsync("config", "include.path", include); byte[] before = await File.ReadAllBytesAsync(include);
        RemotesDialog dialog = await ShowAsync(f.Repository, remote: "inherited");
        try
        {
            GitRemoteSettingsSnapshot remote = Selected(dialog);
            Assert.That(remote.CanModifyStructure, Is.False);
            Assert.That(remote.ModificationRestriction, Is.Not.Null.And.Not.Empty);
            Assert.That(Field<Button>(dialog, "DeleteRemote").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "ToggleRemoteState").IsEnabled, Is.False);
            Assert.That(ToolTip.GetTip(Field<Button>(dialog, "DeleteRemote")), Is.EqualTo(remote.ModificationRestriction));
            Assert.That(ToolTip.GetTip(Field<Button>(dialog, "ToggleRemoteState")), Is.EqualTo(remote.ModificationRestriction));
            dialog.Close(); await Until(() => !dialog.IsVisible);
            Assert.That(await File.ReadAllBytesAsync(include), Is.EqualTo(before));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task LocalBranchPreselectionOpensTrackingAndValidatedEditsPersistImmediatelyWithoutLosingTheNextBaseline()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        const string local = "topic/λ;$()'quoted", merge = "published/λ;$()'quoted";
        await f.GitAsync("branch", local); await f.GitAsync("push", "origin", "main:" + merge); await f.GitAsync("fetch", "origin");
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: local);
        try
        {
            Assert.That(Field<TabControl>(dialog, "RemotesTabs").SelectedIndex, Is.EqualTo(1));
            Assert.That(Field<TextBox>(dialog, "TrackingLocalBranch").Text, Is.EqualTo(local));
            Assert.That(Field<TextBox>(dialog, "TrackingLocalBranch").IsReadOnly, Is.True);
            ComboBox remote = Field<ComboBox>(dialog, "TrackingRemote"), branch = Field<ComboBox>(dialog, "TrackingMergeBranch");
            remote.Focus(); SetChoice(remote, "origin"); branch.Focus();
            await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await TextAsync(f, "config", "branch." + local + ".remote"), Is.EqualTo("origin"));
            Assert.That(await TextAsync(f, "config", "branch." + local + ".merge"), Is.EqualTo("refs/heads/" + local),
                "Choosing a remote defaults an unconfigured merge ref to that local branch, as GitRef does on Windows.");
            branch.Focus(); SetChoice(branch, merge); remote.Focus(); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await TextAsync(f, "config", "branch." + local + ".merge"), Is.EqualTo("refs/heads/" + merge));
            Assert.That(Tracking(dialog).Single(item => item.LocalBranch == local).MergeBranch, Is.EqualTo(merge));
            SetChoice(branch, "second-destination"); Pointer(dialog, "SaveBranchTracking"); await IdleAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, "The first immediate write must advance the optimistic-concurrency baseline. " + dialog.LastException);
            Assert.That(await TextAsync(f, "config", "branch." + local + ".merge"), Is.EqualTo("refs/heads/second-destination"));
            Assert.That(dialog.IsVisible && dialog.HasRepositoryChanges, Is.True);
            Assert.That(PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>(), Is.Empty,
                "Editing tracking does not start a fetch or move any commit.");
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task TrackingSaveAppliesBothPendingFieldsAndBlankValuesUnsetOnlyTheirOwnGitKeys()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("branch", "feature");
        await f.GitAsync("config", "branch.feature.description", "keep description");
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: "feature");
        try
        {
            ComboBox remote = Field<ComboBox>(dialog, "TrackingRemote"), merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
            Button save = Field<Button>(dialog, "SaveBranchTracking"); int saveClicks = 0;
            save.Click += (_, args) => { if (args.Source == save) { saveClicks++; } };
            SetChoice(remote, "origin"); SetChoice(merge, "release/λ"); merge.Focus();
            Pointer(dialog, "SaveBranchTracking"); await SavedTrackingAsync("set both", "origin", "release/λ");
            Assert.That(saveClicks, Is.EqualTo(1), "Both pending values must finish from one Save click, even if a field validation is still queued.");
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await TextAsync(f, "config", "branch.feature.remote"), Is.EqualTo("origin"));
            Assert.That(await TextAsync(f, "config", "branch.feature.merge"), Is.EqualTo("refs/heads/release/λ"));
            SetChoice(merge, ""); Pointer(dialog, "SaveBranchTracking"); await SavedTrackingAsync("unset merge", "origin", null);
            Assert.That(saveClicks, Is.EqualTo(2));
            Assert.That(await ValuesAsync(f, "branch.feature.merge"), Is.Empty);
            Assert.That(await TextAsync(f, "config", "branch.feature.remote"), Is.EqualTo("origin"));
            SetChoice(remote, ""); Pointer(dialog, "SaveBranchTracking"); await SavedTrackingAsync("unset remote", null, null);
            Assert.That(saveClicks, Is.EqualTo(3));
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await ValuesAsync(f, "branch.feature.remote"), Is.Empty);
            Assert.That(await TextAsync(f, "config", "branch.feature.description"), Is.EqualTo("keep description"));
            Assert.That(await TextAsync(f, "rev-parse", "feature"), Is.EqualTo(await TextAsync(f, "rev-parse", "HEAD")));

            async Task SavedTrackingAsync(string stage, string? expectedRemote, string? expectedMerge)
            {
                // IsBusy can become false between queued validations; the refreshed row witnesses their completion.
                int observedClicks = saveClicks;
                await Until(() => !dialog.IsBusy && Tracking(dialog).Any(row => row.LocalBranch == "feature"
                    && row.Remote == expectedRemote && row.MergeBranch == expectedMerge),
                    () => $"stage={stage}; clicks={saveClicks}; busy={dialog.IsBusy}; row={RowState()}; error={dialog.LastError}");
                Assert.That(saveClicks, Is.EqualTo(observedClicks), "Waiting for queued validation must never issue another Save click.");
            }

            string RowState()
            {
                GitBranchTrackingSnapshot? row = Tracking(dialog).SingleOrDefault(row => row.LocalBranch == "feature");
                return JsonSerializer.Serialize(new { LocalBranch = row?.LocalBranch, Remote = row?.Remote, MergeBranch = row?.MergeBranch, ConfigurationRevision = row?.ConfigurationRevision });
            }
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task TrackingEditsAcrossTwoBranchesKeepTheirOwnDraftsAndTheNextRemoteSaveUsesTheLatestSnapshot()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        foreach (string local in new[] { "alpha", "beta" })
        {
            await f.GitAsync("branch", local); await f.GitAsync("config", "branch." + local + ".remote", "origin");
            await f.GitAsync("config", "branch." + local + ".merge", "refs/heads/" + local);
        }
        string head = await TextAsync(f, "rev-parse", "HEAD");
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: "alpha");
        try
        {
            ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
            merge.Focus(); SetChoice(merge, "destination/alpha");
            Field<ListBox>(dialog, "BranchTrackingList").SelectedItem = Tracking(dialog).Single(branch => branch.LocalBranch == "beta");
            Assert.That(Field<TextBox>(dialog, "TrackingLocalBranch").Text, Is.EqualTo("beta"));
            // Edit the next branch before the old branch's asynchronous Validated write has drained.
            SetChoice(Field<ComboBox>(dialog, "TrackingRemote"), "."); SetChoice(merge, "main");
            Pointer(dialog, "SaveBranchTracking");
            await Until(() => !dialog.IsBusy && Tracking(dialog).Single(branch => branch.LocalBranch == "alpha").MergeBranch == "destination/alpha"
                && Tracking(dialog).Single(branch => branch.LocalBranch == "beta") is { Remote: ".", MergeBranch: "main" },
                () => "Error=" + dialog.LastError + "; rows=" + string.Join("; ", Tracking(dialog).Select(branch => branch.LocalBranch + ":" + branch.Remote + "/" + branch.MergeBranch)));
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await TextAsync(f, "config", "branch.alpha.remote"), Is.EqualTo("origin"));
            Assert.That(await TextAsync(f, "config", "branch.alpha.merge"), Is.EqualTo("refs/heads/destination/alpha"));
            Assert.That(await TextAsync(f, "config", "branch.beta.remote"), Is.EqualTo("."));
            Assert.That(await TextAsync(f, "config", "branch.beta.merge"), Is.EqualTo("refs/heads/main"));
            Field<TabControl>(dialog, "RemotesTabs").SelectedIndex = 0; Pump(dialog);
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "renamed"; await SaveAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, "Tracking writes must refresh the expected remote snapshot before a later Save. " + dialog.LastException);
            Assert.That(Selected(dialog).Name, Is.EqualTo("renamed"));
            Assert.That(await TextAsync(f, "config", "branch.alpha.remote"), Is.EqualTo("renamed"));
            Assert.That(await TextAsync(f, "config", "branch.alpha.merge"), Is.EqualTo("refs/heads/destination/alpha"));
            Assert.That(await TextAsync(f, "config", "branch.beta.remote"), Is.EqualTo("."));
            Assert.That(await TextAsync(f, "config", "branch.beta.merge"), Is.EqualTo("refs/heads/main"));
            Assert.That(await TextAsync(f, "rev-parse", "refs/remotes/renamed/main"), Is.EqualTo(head));
            Assert.That(await TextAsync(f, "rev-parse", "HEAD"), Is.EqualTo(head));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task TheAutomaticFetchRefreshesMergeChoicesWithoutClosingOrReopeningTheManager()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = f.Output("new-server.git");
        await f.GitAsync("clone", "--bare", f.Repository.Path, bare);
        await f.GitAsync("--git-dir=" + bare, "branch", "fresh/remote-only", "main");
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "origin"; Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = bare;
            await SaveAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(await TextAsync(f, "rev-parse", "refs/remotes/origin/fresh/remote-only"), Is.EqualTo(await TextAsync(f, "rev-parse", "HEAD")));
            Field<TabControl>(dialog, "RemotesTabs").SelectedIndex = 1; Pump(dialog);
            ComboBox remote = Field<ComboBox>(dialog, "TrackingRemote"), merge = Field<ComboBox>(dialog, "TrackingMergeBranch");
            remote.Focus(); SetChoice(remote, "origin"); merge.Focus();
            await Until(() => !dialog.IsBusy && Tracking(dialog).Single(branch => branch.LocalBranch == "main").Remote == "origin");
            merge.IsDropDownOpen = true; Pump(dialog);
            Assert.That(merge.Items.OfType<string>(), Does.Contain("main").And.Contain("fresh/remote-only"),
                "The original FormRemotes initializes after its automatic fetch, so newly fetched refs must already be selectable.");
            merge.IsDropDownOpen = false;
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task FailedStaleTrackingEditCanBeAbandonedByClosingWithoutOverwritingExternalConfiguration()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: "main");
        try
        {
            await f.GitAsync("config", "remote.origin.external-note", "keep this external edit"); byte[] external = await ConfigBytesAsync(f);
            ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch"); merge.Focus(); SetChoice(merge, "unsaved/draft");
            Pointer(dialog, "SaveBranchTracking"); await Until(() => !dialog.IsBusy && dialog.LastError is not null);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That(merge.Text, Is.EqualTo("unsaved/draft"));
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(external));
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.OwnedWindows, Is.Empty, "A failed tracking validation must not trap the owner in an endless dirty-close retry.");
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(external));
            Assert.That(await TextAsync(f, "config", "branch.main.merge"), Is.EqualTo("refs/heads/main"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task ATrackingWriteKeepsAllActiveRemotesBeforeTheInactiveGroup()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.Repository.AddRemoteAsync("zeta", bare); await DisabledAsync(f, "aaa-inactive", bare); await DisabledAsync(f, "zzz-inactive", bare);
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: "main");
        try
        {
            ComboBox remote = Field<ComboBox>(dialog, "TrackingRemote"); remote.Focus(); SetChoice(remote, "origin");
            Field<ComboBox>(dialog, "TrackingMergeBranch").Focus();
            await Until(() => !dialog.IsBusy && Tracking(dialog).Single(branch => branch.LocalBranch == "main").Remote == "origin");
            Field<TabControl>(dialog, "RemotesTabs").SelectedIndex = 0; Pump(dialog);
            Assert.That(Remotes(dialog).Select(item => (item.Name, item.Disabled)), Is.EqualTo(new[]
            {
                ("origin", false), ("zeta", false), ("aaa-inactive", true), ("zzz-inactive", true)
            }));
            TextBlock active = Field<TextBlock>(dialog, "ActiveRemotesHeader"), inactive = Field<TextBlock>(dialog, "InactiveRemotesHeader");
            Assert.That(active.Text, Is.EqualTo("Active")); Assert.That(inactive.Text, Is.EqualTo("Inactive"));
            Assert.That(active.TranslatePoint(default, dialog)!.Value.Y, Is.LessThan(inactive.TranslatePoint(default, dialog)!.Value.Y));
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task OriginalFieldMnemonicsUseLabelTargetsAndFocusTheActualEditorsOnBothTabs()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        RemotesDialog dialog = await ShowAsync(f.Repository, advanced: true);
        try
        {
            foreach (string name in new[] { "RemoteSettingsUrl", "RemoteSettingsName", "RemoteSettingsPrefix", "RemoteSettingsPushUrl" })
            {
                Control editor = Field<Control>(dialog, name);
                Label label = dialog.GetVisualDescendants().OfType<Label>().Single(candidate => candidate.Target == editor);
                Assert.That(label.Content, Is.TypeOf<AccessText>());
            }
            Alt(dialog, PhysicalKey.U); Assert.That(HasFocus(Field<ComboBox>(dialog, "RemoteSettingsUrl")), Is.True,
                "Focused=" + dialog.FocusManager?.GetFocusedElement()?.GetType().FullName + "/" + (dialog.FocusManager?.GetFocusedElement() as Control)?.Name + "; URL focusable=" + Field<ComboBox>(dialog, "RemoteSettingsUrl").Focusable
                + "; captions=" + string.Join("; ", dialog.GetVisualDescendants().OfType<AccessText>().Select(text => text.Text + "[" + text.AccessKey + "] visible=" + text.IsEffectivelyVisible)));
            Alt(dialog, PhysicalKey.N); Assert.That(HasFocus(Field<TextBox>(dialog, "RemoteSettingsName")), Is.True);
            Field<TabControl>(dialog, "RemotesTabs").SelectedIndex = 1; Pump(dialog);
            foreach (string name in new[] { "TrackingLocalBranch", "TrackingRemote", "TrackingMergeBranch" })
            {
                Control editor = Field<Control>(dialog, name);
                Assert.That(dialog.GetVisualDescendants().OfType<Label>().Single(candidate => candidate.Target == editor).Content, Is.TypeOf<AccessText>());
            }
            Alt(dialog, PhysicalKey.L); Assert.That(HasFocus(Field<TextBox>(dialog, "TrackingLocalBranch")), Is.True);
            Alt(dialog, PhysicalKey.R); Assert.That(HasFocus(Field<ComboBox>(dialog, "TrackingRemote")), Is.True);
            Alt(dialog, PhysicalKey.D); Assert.That(HasFocus(Field<ComboBox>(dialog, "TrackingMergeBranch")), Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task MergeChoicesMatchTheExactSelectedRemoteAndExcludeSymbolicHeadAndSubstringNeighbours()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.Repository.AddRemoteAsync("myorigin", bare); await f.GitAsync("fetch", "myorigin");
        await f.GitAsync("update-ref", "refs/remotes/myorigin/foreign-only", "HEAD");
        await f.GitAsync("update-ref", "refs/remotes/origin/original-only", "HEAD");
        await f.GitAsync("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        await f.GitAsync("config", "branch.main.remote", "origin"); await f.GitAsync("config", "branch.main.merge", "refs/heads/main");
        RemotesDialog dialog = await ShowAsync(f.Repository, branch: "main");
        try
        {
            ComboBox merge = Field<ComboBox>(dialog, "TrackingMergeBranch"); merge.IsDropDownOpen = true; Pump(dialog);
            Assert.That(merge.Items.OfType<string>(), Does.Contain("").And.Contain("main").And.Contain("original-only")
                .And.Not.Contain("foreign-only").And.Not.Contain("HEAD"));
            merge.IsDropDownOpen = false;
            ComboBox remote = Field<ComboBox>(dialog, "TrackingRemote");
            remote.Focus(); SetChoice(remote, "myorigin"); merge.Focus(); await IdleAsync(dialog);
            merge.IsDropDownOpen = true; Pump(dialog);
            Assert.That(merge.Items.OfType<string>(), Does.Contain("foreign-only").And.Not.Contain("original-only"));
            merge.IsDropDownOpen = false;
            Assert.That(await TextAsync(f, "config", "branch.main.remote"), Is.EqualTo("myorigin"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task AdvancedOptionsAreHiddenByDefaultAndSavedPrefixIsUsedByTheExistingPushDestination()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync(); await f.GitAsync("branch", "feature");
        await f.GitAsync("config", "branch.feature.remote", "origin");
        await f.GitAsync("config", "remote.origin.color", "#456789");
        RemotesDialog ordinary = await ShowAsync(f.Repository);
        try
        {
            Assert.That(Field<TextBox>(ordinary, "RemoteSettingsPrefix").IsEffectivelyVisible, Is.False);
            Assert.That(Field<Button>(ordinary, "RemoteSettingsColor").IsEffectivelyVisible, Is.False);
            Assert.That(Field<Button>(ordinary, "ResetRemoteSettingsColor").IsEffectivelyVisible, Is.False);
        }
        finally { await CloseAsync(ordinary); }
        RemotesDialog advanced = await ShowAsync(f.Repository, advanced: true);
        try
        {
            Field<TextBox>(advanced, "RemoteSettingsPrefix").Text = "review/";
            Pointer(advanced, "ResetRemoteSettingsColor");
            await SaveAsync(advanced);
            Assert.That(advanced.LastError, Is.Null, advanced.LastException?.ToString());
            Assert.That(Selected(advanced).Prefix, Is.EqualTo("review/"));
            Assert.That(await TextAsync(f, "config", "remote.origin.prefix"), Is.EqualTo("review/"));
            Assert.That(await ValuesAsync(f, "remote.origin.color"), Is.Empty);
        }
        finally { await CloseAsync(advanced); }
        await f.GitAsync("checkout", "feature");
        PushDialog push = new(f.Repository); push.Show();
        try
        {
            await push.InitializeAsync(); await IdleAsync(push);
            Assert.That(Field<AutoCompleteBox>(push, "PushRemoteBranch").Text, Is.EqualTo("review/feature"));
            Assert.That(push.Attempted, Is.False);
        }
        finally { await CloseAsync(push); }
    }

    [AvaloniaTest]
    public async Task SelectionAndNewDiscardOnlyUnwrittenEditsAndDefaultSelectionPrefersAnActiveRemote()
    {
        using Fixture f = await Fixture.CreateAsync(); string bare = await f.CreateRemoteAsync();
        await f.Repository.AddRemoteAsync("zeta", bare); await DisabledAsync(f, "aaa-inactive", bare);
        byte[] before = await ConfigBytesAsync(f);
        RemotesDialog dialog = await ShowAsync(f.Repository);
        try
        {
            Assert.That(Selected(dialog).Disabled, Is.False);
            Select(dialog, "origin"); Field<TextBox>(dialog, "RemoteSettingsName").Text = "discarded rename";
            Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = "discarded url";
            Select(dialog, "zeta"); Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").Text, Is.EqualTo("zeta"));
            Select(dialog, "origin"); Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").Text, Is.EqualTo(bare));
            Pointer(dialog, "NewRemote");
            Assert.That(Field<ListBox>(dialog, "RemoteSettingsList").SelectedItem, Is.Null);
            Assert.That(Field<TextBox>(dialog, "RemoteSettingsName").Text, Is.Null.Or.Empty);
            Assert.That(Field<ComboBox>(dialog, "RemoteSettingsUrl").Text, Is.Null.Or.Empty);
            Assert.That(Field<Button>(dialog, "DeleteRemote").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "SaveRemoteSettings").IsEnabled, Is.False);
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
        }
        finally { await CloseAsync(dialog); }
        RemotesDialog specific = await ShowAsync(f.Repository, remote: "aaa-inactive");
        try { Assert.That(Selected(specific).Name, Is.EqualTo("aaa-inactive")); Assert.That(Selected(specific).Disabled, Is.True); }
        finally { await CloseAsync(specific); }
    }

    [AvaloniaTest]
    public async Task ColorPickerValidatesOpaqueColorsAndCancelOrAcceptOnlyChangesTheBufferUntilSave()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        RemotesDialog dialog = await ShowAsync(f.Repository, advanced: true);
        try
        {
            byte[] before = await ConfigBytesAsync(f);
            Pointer(dialog, "RemoteSettingsColor"); await Until(() => dialog.OwnedWindows.Any(window => window.Name == "RemoteColorPicker"));
            Window picker = dialog.OwnedWindows.Single(window => window.Name == "RemoteColorPicker");
            TextBox hex = Field<TextBox>(picker, "RemoteColorValue"); hex.Text = "not-a-color";
            Assert.That(Field<Button>(picker, "AcceptRemoteColor").IsEnabled, Is.False);
            hex.Text = "#80123456"; Assert.That(Field<Button>(picker, "AcceptRemoteColor").IsEnabled, Is.False);
            hex.Text = "#123456"; Assert.That(Field<Button>(picker, "AcceptRemoteColor").IsEnabled, Is.True);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                picker.RequestedThemeVariant = theme;
                AssertInside(picker, hex, Field<Slider>(picker, "RemoteColorRed"), Field<Slider>(picker, "RemoteColorGreen"),
                    Field<Slider>(picker, "RemoteColorBlue"), Field<Button>(picker, "AcceptRemoteColor"), Field<Button>(picker, "CancelRemoteColor"));
                Capture(picker, "remotes-color-picker-" + theme + ".png");
            }
            Pointer(picker, "CancelRemoteColor"); await Until(() => dialog.OwnedWindows.Count == 0);
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
            Assert.That(Field<Button>(dialog, "ResetRemoteSettingsColor").IsVisible, Is.False);
            Pointer(dialog, "RemoteSettingsColor"); await Until(() => dialog.OwnedWindows.Any(window => window.Name == "RemoteColorPicker"));
            picker = dialog.OwnedWindows.Single(window => window.Name == "RemoteColorPicker");
            Field<TextBox>(picker, "RemoteColorValue").Text = "#123456"; Pointer(picker, "AcceptRemoteColor");
            await Until(() => dialog.OwnedWindows.Count == 0);
            Assert.That(await ConfigBytesAsync(f), Is.EqualTo(before));
            Assert.That(Field<Button>(dialog, "ResetRemoteSettingsColor").IsVisible, Is.True);
            Assert.That(dialog.HasRepositoryChanges, Is.False);
            await SaveAsync(dialog);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(await TextAsync(f, "config", "remote.origin.color"), Is.EqualTo("#123456"));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task PullAndPushManageRemotesOpenTheSameWindowWithTheSelectedRemoteAndReloadItsSavedRename()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        string head = await TextAsync(f, "rev-parse", "HEAD");
        foreach (bool fromPush in new[] { false, true })
        {
            string previous = fromPush ? "upstream" : "origin", renamed = fromPush ? "origin" : "upstream";
            RepositoryDialog owner = fromPush ? new PushDialog(f.Repository) : new PullDialog(f.Repository, GitPullAction.Fetch, previous);
            owner.Show();
            try
            {
                await owner.InitializeAsync(); await IdleAsync(owner);
                if (fromPush) { Field<ComboBox>(owner, "PushRemote").SelectedItem = previous; }
                Pointer(owner, fromPush ? "PushManageRemotes" : "PullManageRemotes");
                await Until(() => owner.OwnedWindows.OfType<RemotesDialog>().Any(ManagerReady));
                RemotesDialog manager = owner.OwnedWindows.OfType<RemotesDialog>().Single();
                Assert.That(Selected(manager).Name, Is.EqualTo(previous));
                Assert.That(Field<TabControl>(manager, "RemotesTabs").SelectedIndex, Is.EqualTo(0));
                Field<TextBox>(manager, "RemoteSettingsName").Text = renamed; await SaveAsync(manager);
                Assert.That(manager.LastError, Is.Null, manager.LastException?.ToString());
                manager.Close(); await IdleAsync(owner);
                Assert.That(owner.LastError, Is.Null, owner.LastException?.ToString());
                ComboBox choice = Field<ComboBox>(owner, fromPush ? "PushRemote" : "PullRemote");
                Assert.That(fromPush ? choice.SelectedItem : choice.Text, Is.EqualTo(renamed));
                Assert.That(owner.HasRepositoryChanges, Is.True);
                Assert.That(await TextAsync(f, "rev-parse", "HEAD"), Is.EqualTo(head));
            }
            finally { await CloseAsync(owner); }
        }
    }

    [AvaloniaTest]
    public async Task RepositoryMenuUsesTheOriginalRemoteIconAndOpensTheCompleteManager()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        MainWindow main = new() { Width = 1280, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            MenuItem repository = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => Header(item.Header) == "Repository");
            MenuItem action = repository.Items.OfType<MenuItem>().Single(item => item.Name == "manageRemoteRepositoriesToolStripMenuItem1");
            Assert.That(action.IsEnabled, Is.True); Assert.That(action.Icon, Is.TypeOf<Image>());
            Assert.That(((Image)action.Icon!).Tag, Is.EqualTo("Remotes.png"));
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => main.OwnedWindows.OfType<RemotesDialog>().Any(ManagerReady));
            RemotesDialog manager = main.OwnedWindows.OfType<RemotesDialog>().Single();
            Assert.That(Field<TabControl>(manager, "RemotesTabs").Items.Count, Is.EqualTo(2));
            Assert.That(Selected(manager).Name, Is.EqualTo("origin"));
            manager.Close(); await Until(() => !main.IsBusy && !manager.IsVisible);
            Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task AdvancedOptionsSettingPersistsOnlyOnOkAndControlsNewManagerWindowsAcrossReopens()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.CreateRemoteAsync();
        MainWindow main = new() { Width = 1280, Height = 800 }; main.Show();
        try
        {
            await main.OpenRepositoryAsync(f.Repository.Path); await Until(() => !main.IsBusy);
            Assert.That(new PreferencesStore().Load().AlwaysShowAdvancedOptions, Is.False);
            DesktopSettingsDialog settings = await OpenSettingsAsync(main);
            CheckBox advanced = Field<CheckBox>(settings, "SettingsAdvancedOptions");
            Assert.That(Header(advanced.Content), Is.EqualTo("Always show advanced options")); Assert.That(advanced.IsChecked, Is.False);
            advanced.IsChecked = true; Pointer(settings, "CancelDesktopSettings"); await Until(() => !settings.IsVisible);
            Assert.That(new PreferencesStore().Load().AlwaysShowAdvancedOptions, Is.False);
            settings = await OpenSettingsAsync(main); Field<CheckBox>(settings, "SettingsAdvancedOptions").IsChecked = true;
            Pointer(settings, "SaveDesktopSettings"); await Until(() => !settings.IsVisible && new PreferencesStore().Load().AlwaysShowAdvancedOptions);
            RemotesDialog shown = await ShowAsync(f.Repository);
            try
            {
                Assert.That(Field<TextBox>(shown, "RemoteSettingsPrefix").IsEffectivelyVisible, Is.True);
                Assert.That(Field<Button>(shown, "RemoteSettingsColor").IsEffectivelyVisible, Is.True);
            }
            finally { await CloseAsync(shown); }
            settings = await OpenSettingsAsync(main); Assert.That(Field<CheckBox>(settings, "SettingsAdvancedOptions").IsChecked, Is.True);
            Field<CheckBox>(settings, "SettingsAdvancedOptions").IsChecked = false; Pointer(settings, "SaveDesktopSettings");
            await Until(() => !settings.IsVisible && !new PreferencesStore().Load().AlwaysShowAdvancedOptions);
            shown = await ShowAsync(f.Repository);
            try
            {
                Assert.That(Field<TextBox>(shown, "RemoteSettingsPrefix").IsEffectivelyVisible, Is.False);
                Assert.That(Field<Button>(shown, "RemoteSettingsColor").IsEffectivelyVisible, Is.False);
            }
            finally { await CloseAsync(shown); }
        }
        finally { await CloseAsync(main); }
    }

    [AvaloniaTest]
    public async Task ClosingTheManagerCancelsItsOwnedFetchAndDrainsTheNativeProcessTreeWhileKeepingTheSavedRemote()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("Private native Git process and procfs require Linux."); return; }
        using Fixture f = await Fixture.CreateAsync(); string bare = f.Output("slow-server.git");
        await f.GitAsync("clone", "--bare", f.Repository.Path, bare);
        string actors = f.Output("fetch-actors"), wrapper = f.Output("fetch-git");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfetch=\nfor arg do\n if test \"$arg\" = fetch; then fetch=1; fi\ndone\n"
            + "if test \"$fetch\" = 1; then\n sleep 60 &\n child=$!\n printf '%s %s\\n' \"$$\" \"$child\" > " + ShellQuote(actors)
            + "\n wait\nfi\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper));
        RemotesDialog dialog = await ShowAsync(repository); int[] pids = [];
        try
        {
            Field<TextBox>(dialog, "RemoteSettingsName").Text = "slow"; Field<ComboBox>(dialog, "RemoteSettingsUrl").Text = bare;
            Pointer(dialog, "SaveRemoteSettings"); await Until(() => ReadActors(actors).Length == 2);
            pids = ReadActors(actors);
            Assert.That(dialog.IsBusy, Is.True);
            Assert.That(PushProcessTestDriver.Windows(dialog).OfType<GitProcessDialog>().Any(process => process.IsRunning), Is.True);
            dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(pids.Any(ActorAlive), Is.False, "Closing the owner must wait for its private Git wrapper and child actor to stop.");
            Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(await TextAsync(f, "config", "remote.slow.url"), Is.EqualTo(bare), "The completed Save is preserved when the subsequent fetch is cancelled.");
            Assert.That(await ValuesAsync(f, "branch.main.remote"), Is.Empty);
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            GitResult fetched = await new GitRunner().RunAsync(f.Repository.Path, ["show-ref", "--verify", "refs/remotes/slow/main"]);
            Assert.That(fetched.Success, Is.False);
        }
        finally
        {
            await CloseAsync(dialog);
            foreach (int pid in pids.Where(ActorAlive))
            {
                try { using Process process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }
    }

    private static async Task<RemotesDialog> ShowAsync(GitRepository repository, string? remote = null, string? branch = null, bool advanced = false)
    {
        RemotesDialog dialog = new(repository, remote, branch, advanced); dialog.Show(); await dialog.InitializeAsync(); await IdleAsync(dialog); Pump(dialog);
        Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog;
    }
    private static GitRemoteSettingsSnapshot[] Remotes(RemotesDialog dialog) => Field<ListBox>(dialog, "RemoteSettingsList").Items.OfType<GitRemoteSettingsSnapshot>().ToArray();
    private static bool ManagerReady(RemotesDialog dialog) => !dialog.IsBusy && dialog.GetVisualDescendants().OfType<ListBox>().Any(list => list.Name == "RemoteSettingsList" && list.Items.Count != 0);
    private static GitRemoteSettingsSnapshot Selected(RemotesDialog dialog) => (GitRemoteSettingsSnapshot)Field<ListBox>(dialog, "RemoteSettingsList").SelectedItem!;
    private static GitBranchTrackingSnapshot[] Tracking(RemotesDialog dialog) => Field<ListBox>(dialog, "BranchTrackingList").Items.OfType<GitBranchTrackingSnapshot>().ToArray();
    private static void Select(RemotesDialog dialog, string name)
    {
        Field<ListBox>(dialog, "RemoteSettingsList").SelectedItem = Remotes(dialog).Single(remote => remote.Name == name); Pump(dialog);
    }
    private static void SetChoice(ComboBox box, string text) { box.SelectedItem = text; box.Text = text; }
    private static async Task SaveAsync(RemotesDialog dialog)
    {
        Pointer(dialog, "SaveRemoteSettings"); await PushProcessTestDriver.UntilAsync(dialog, () => !dialog.IsBusy,
            "The remote Save and its owned fetch must settle."); Pump(dialog);
    }
    private static async Task<GitProcessDialog> FinishedProcessAsync(Window owner)
    {
        await Until(() => PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Any(process => !process.IsRunning && process.Result is not null));
        return PushProcessTestDriver.Windows(owner).OfType<GitProcessDialog>().Single();
    }
    private static async Task<Window> QuestionAsync(Window owner)
    {
        await Until(() => owner.OwnedWindows.Any(window => window.GetVisualDescendants().OfType<Button>().Any(button => Header(button.Content) == "No")));
        return owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<Button>().Any(button => Header(button.Content) == "No"));
    }
    private static async Task<DesktopSettingsDialog> OpenSettingsAsync(MainWindow main)
    {
        MenuItem tools = main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => Header(item.Header) == "Tools");
        tools.Items.OfType<MenuItem>().Single(item => item.Name == "settingsToolStripMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any());
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(); Pump(settings); SettingsNavigationTestDriver.Select(settings, "Advanced"); return settings;
    }
    private static Button CaptionButton(Window owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Header(button.Content) == caption);
    private static string Header(object? content) => content switch { string value => value.Replace("_", "", StringComparison.Ordinal), AccessText access => (access.Text ?? "").Replace("_", "", StringComparison.Ordinal), TextBlock block => block.Text ?? "", _ => content?.ToString() ?? "" };
    private static T Field<T>(TopLevel owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Window owner, string name) => PushProcessTestDriver.Pointer(Field<Button>(owner, name));
    private static bool HasFocus(Control control) => control.IsFocused || control.GetVisualDescendants().OfType<Control>().Any(child => child.IsFocused);
    private static void Alt(Window owner, PhysicalKey key)
    {
        Pump(owner); owner.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.Alt); owner.KeyPressQwerty(key, RawInputModifiers.Alt);
        if (owner.IsVisible)
        {
            owner.KeyReleaseQwerty(key, RawInputModifiers.Alt); owner.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task IdleAsync(RepositoryDialog dialog) => await Until(() => !dialog.IsBusy,
        () => "Busy=" + dialog.IsBusy + "; error=" + dialog.LastError + "; windows=" + string.Join(", ", PushProcessTestDriver.Windows(dialog).Select(window => window.Name)));
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The real remote-management workflow did not settle within 20 seconds. " + (condition() ? "" : diagnostic?.Invoke()));
    }
    private static async Task CloseAsync(Window window)
    {
        foreach (Window child in window.OwnedWindows.ToArray()) { await CloseAsync(child); }
        if (window.IsVisible) { window.Close(); }
        await Until(() => !window.IsVisible && (window is not RepositoryDialog dialog || !dialog.IsBusy)); Pump(window);
    }
    private static void Pump(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static void Capture(Window window, string filename)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, filename); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
    private static void AssertInside(Window window, params Control[] controls)
    {
        Pump(window);
        foreach (Control control in controls)
        {
            Assert.That(control.IsEffectivelyVisible, Is.True, control.Name); Assert.That(control.Bounds.Width, Is.GreaterThan(0), control.Name);
            Assert.That(control.Bounds.Height, Is.GreaterThan(0), control.Name); Point origin = control.TranslatePoint(default, window)!.Value;
            Assert.That(origin.X, Is.GreaterThanOrEqualTo(-.5), control.Name); Assert.That(origin.Y, Is.GreaterThanOrEqualTo(-.5), control.Name);
            Assert.That(origin.X + control.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width + .5), control.Name);
            Assert.That(origin.Y + control.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height + .5), control.Name);
        }
    }
    private static async Task DisabledAsync(Fixture fixture, string name, string url)
        => await File.AppendAllTextAsync(fixture.PathInRepository(".git/config"), "\n[-remote " + GitQuote(name) + "]\n\turl = " + GitQuote(url) + "\n\tfetch = +refs/heads/*:refs/remotes/" + name + "/*\n");
    private static string GitQuote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static Task<byte[]> ConfigBytesAsync(Fixture fixture) => File.ReadAllBytesAsync(fixture.PathInRepository(".git/config"));
    private static async Task<string> TextAsync(Fixture fixture, params string[] arguments) => (await fixture.GitAsync(arguments)).StandardOutput.TrimEnd('\r', '\n');
    private static async Task<string[]> ValuesAsync(Fixture fixture, string key)
    {
        GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, ["config", "--null", "--get-all", key]);
        Assert.That(result.ExitCode, Is.EqualTo(0).Or.EqualTo(1), result.StandardError);
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    private static int[] ReadActors(string path)
    {
        try { return File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray(); }
        catch (Exception error) when (error is IOException or FormatException) { return []; }
    }
    private static bool ActorAlive(int pid)
    {
        try { string state = File.ReadAllText("/proc/" + pid + "/stat"); int end = state.LastIndexOf(')'); return end < 0 || state[end + 2] != 'Z'; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
