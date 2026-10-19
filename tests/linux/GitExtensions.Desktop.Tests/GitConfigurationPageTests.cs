using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using ReferenceFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class GitConfigurationPageTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OriginalFieldsLoadWithReadOnlyScopesAndUntouchedOkKeepsExactConfiguration(bool dark)
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "user.name", "Local Ω Name"); await f.Set("local", "user.email", "local@example.invalid");
        await f.Set("local", "credential.helper", "cache --timeout=3600"); await f.Set("local", "core.editor", "custom-editor --wait 'literal'");
        await f.Set("local", "commit.template", "/a template/'literal.txt"); await f.Set("local", "core.autocrlf", "input");
        await f.Set("local", "i18n.filesencoding", "utf-8"); await f.Set("local", "diff.guitool", "meld");
        await f.Set("system", "user.name", "System name");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(dark: dark);
        Assert.That(dialog.Title, Is.EqualTo("Config")); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("Local Ω Name"));
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").IsReadOnly, Is.True);
        Assert.That(Field<TextBox>(dialog, "ReadOnlyGlobalEditor").Text, Is.EqualTo("custom-editor --wait 'literal'"));
        Assert.That(Field<TextBlock>(dialog, "lblCredentialHelper").IsVisible, Is.False);
        Assert.That(Field<ComboBox>(dialog, "cbxCredentialHelper").IsVisible, Is.False);
        Assert.That(Field<TextBox>(dialog, "ReadOnlycbxCredentialHelper").IsVisible, Is.False);
        Assert.That(Field<RadioButton>(dialog, "globalAutoCrlfInput").IsChecked, Is.True);
        Assert.That(SelectedEncoding(dialog), Is.EqualTo("utf-8"));
        await ChooseScope(dialog, 3);
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("System name"));
        Assert.That(Field<TextBox>(dialog, "txtCommitTemplatePath").IsReadOnly, Is.True);
        Assert.That(Field<Button>(dialog, "btnCommitTemplateBrowse").IsEnabled, Is.False);
        Assert.That(Field<RadioButton>(dialog, "globalAutoCrlfTrue").IsEnabled, Is.False);
        await ChooseScope(dialog, 1);
        Assert.That(Field<ComboBox>(dialog, "cbxCredentialHelper").Text, Is.EqualTo("cache --timeout=3600"));
        Assert.That(Field<ComboBox>(dialog, "GlobalEditor").IsEditable, Is.True);
        Assert.That(Field<TextBox>(dialog, "txtCommitTemplatePath").Text, Is.EqualTo("/a template/'literal.txt"));
        Assert.That(ToolTip.GetTip(Field<TextBox>(dialog, "GlobalUserName"))?.ToString(), Does.Contain(".git/config"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        AssertOriginalColumnsAligned(dialog);
        Capture(dialog, "git-config-full-" + (dark ? "dark" : "light") + ".png");
        Click(dialog, "GitToolsAccept"); await Until(() => done.IsCompleted); Assert.That(await done, Is.True);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(dialog.MayHaveChangedConfiguration, Is.False);
    }

    [AvaloniaTest]
    public async Task ScalarDraftsStayIndependentAcrossScopesAndCancelPublishesNothing()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Set("local", "user.name", "published local"); await f.Set("global", "user.name", "published global");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ChooseScope(dialog, 1); Edit(dialog, "GlobalUserName", "local pending"); Edit(dialog, "GlobalUserEmail", "pending@example.invalid");
        EditCombo(dialog, "GlobalEditor", "editor --literal \"$FILE\""); Edit(dialog, "txtCommitTemplatePath", "/pending template.txt");
        Click(dialog, "globalAutoCrlfInput"); ChooseEncoding(dialog, "utf-8");
        await ChooseScope(dialog, 2); Edit(dialog, "GlobalUserName", "global pending"); EditCombo(dialog, "cbxCredentialHelper", "custom-helper --flag");
        await ChooseScope(dialog, 0); Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("published local"));
        await ChooseScope(dialog, 1);
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("local pending"));
        Assert.That(Field<TextBox>(dialog, "GlobalUserEmail").Text, Is.EqualTo("pending@example.invalid"));
        Assert.That(Field<ComboBox>(dialog, "GlobalEditor").Text, Is.EqualTo("editor --literal \"$FILE\""));
        Assert.That(Field<TextBox>(dialog, "txtCommitTemplatePath").Text, Is.EqualTo("/pending template.txt"));
        Assert.That(Field<RadioButton>(dialog, "globalAutoCrlfInput").IsChecked, Is.True); Assert.That(SelectedEncoding(dialog), Is.EqualTo("utf-8"));
        await ChooseScope(dialog, 2); Assert.That(Field<ComboBox>(dialog, "cbxCredentialHelper").Text, Is.EqualTo("custom-helper --flag"));
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("global pending"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False); Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task ApplySavesAllSevenFieldsAndToolsTogetherThenCancelKeepsPublishedValues()
    {
        await using Scope f = await Scope.CreateAsync(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        await ChooseScope(dialog, 1); Edit(dialog, "GlobalUserName", "Exact Ω ' name"); Edit(dialog, "GlobalUserEmail", "literal@example.invalid");
        EditCombo(dialog, "cbxCredentialHelper", "!helper --literal '$USER'"); EditCombo(dialog, "GlobalEditor", "editor --wait '/literal path'");
        Edit(dialog, "txtCommitTemplatePath", "/literal ' template\\file.txt"); Click(dialog, "globalAutoCrlfFalse"); ChooseEncoding(dialog, "utf-8");
        EditCombo(dialog, "_NO_TRANSLATE_cboDiffTool", "custom tool"); Edit(dialog, "txtDiffToolCommand", "diff --literal \"$LOCAL\"");
        await ChooseScope(dialog, 2); Edit(dialog, "GlobalUserEmail", "global@example.invalid");
        Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastError, Does.Contain("Global: user.email").And.Contain("another effective value still wins"));
        Assert.That(dialog.LastSaveResult?.Success, Is.True); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Assert.That(await f.Read("local", "user.name"), Is.EqualTo("Exact Ω ' name"));
        Assert.That(await f.Read("local", "user.email"), Is.EqualTo("literal@example.invalid"));
        Assert.That(await f.Read("local", "credential.helper"), Is.EqualTo("!helper --literal '$USER'"));
        Assert.That(await f.Read("local", "core.editor"), Is.EqualTo("editor --wait '/literal path'"));
        Assert.That(await f.Read("local", "commit.template"), Is.EqualTo("/literal ' template\\file.txt"));
        Assert.That(await f.Read("local", "core.autocrlf"), Is.EqualTo("false")); Assert.That(await f.Read("local", "i18n.filesencoding"), Is.EqualTo("utf-8"));
        Assert.That(await f.Read("local", "difftool.custom tool.cmd"), Is.EqualTo("diff --literal \"$LOCAL\""));
        Assert.That(await f.Read("global", "user.email"), Is.EqualTo("global@example.invalid"));
        await ChooseScope(dialog, 0); Assert.That(Field<TextBox>(dialog, "GlobalUserEmail").Text, Is.EqualTo("literal@example.invalid"));
        await ChooseScope(dialog, 2);
        Edit(dialog, "GlobalUserEmail", "discarded@example.invalid"); Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(await f.Read("global", "user.email"), Is.EqualTo("global@example.invalid"));
        (GitToolsSettingsDialog reopened, Task<bool> reopenedDone) = await f.Open(); await ChooseScope(reopened, 1);
        Assert.That(Field<TextBox>(reopened, "GlobalUserName").Text, Is.EqualTo("Exact Ω ' name"));
        Assert.That(Field<RadioButton>(reopened, "globalAutoCrlfFalse").IsChecked, Is.True);
        Click(reopened, "GitToolsCancel"); Assert.That(await reopenedDone, Is.False);
    }

    [AvaloniaTest]
    [TestCase("YES", "globalAutoCrlfTrue")]
    [TestCase("off", "globalAutoCrlfFalse")]
    [TestCase("unrecognized", null)]
    public async Task UnknownEncodingAutocrlfAliasesAndEmptyHelperRemainLiteralOnUntouchedOk(string autocrlf, string? selectedRadio)
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "core.autocrlf", autocrlf);
        await f.Set("local", "i18n.filesencoding", "Unknown.Raw Encoding Ω"); await f.Set("local", "credential.helper", "");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(allowUnavailable: selectedRadio is null); await ChooseScope(dialog, 1);
        Assert.That(SelectedEncoding(dialog), Is.EqualTo("Unknown.Raw Encoding Ω"));
        RadioButton[] radios = [Field<RadioButton>(dialog, "globalAutoCrlfTrue"), Field<RadioButton>(dialog, "globalAutoCrlfInput"), Field<RadioButton>(dialog, "globalAutoCrlfFalse"), Field<RadioButton>(dialog, "globalAutoCrlfNotSet")];
        Assert.That(radios.Where(radio => radio.IsChecked == true).Select(radio => radio.Name), Is.EqualTo(selectedRadio is null ? Array.Empty<string>() : [selectedRadio]));
        Assert.That(Field<TextBlock>(dialog, "GitAutoCrlfNotice").IsVisible, Is.EqualTo(selectedRadio is null));
        await ChooseScope(dialog, 2); await ChooseScope(dialog, 1); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        if (selectedRadio is null)
        {
            Assert.That(dialog.IsReady, Is.False); Assert.That(dialog.LastError, Does.Contain("core.autocrlf"));
            Assert.That(Field<TextBlock>(dialog, "GitAutoCrlfNotice").Text, Does.Contain(autocrlf));
            Assert.That(Field<TextBox>(dialog, "GlobalUserName").IsReadOnly, Is.True);
            Assert.That(Field<RadioButton>(dialog, "globalAutoCrlfNotSet").IsEnabled, Is.False);
            Assert.That(Field<Button>(dialog, "GitToolsAccept").IsEnabled, Is.False);
            Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        }
        else { Click(dialog, "GitToolsAccept"); await Until(() => done.IsCompleted); Assert.That(await done, Is.True); }
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
    }

    [AvaloniaTest]
    public async Task MultipleCredentialHelpersAreReadOnlyAndUnrelatedApplyRetainsEveryOccurrence()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "credential.helper", "");
        await f.Git.GitAsync("config", "--local", "--add", "credential.helper", "cache");
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Assert.That(Field<ComboBox>(dialog, "cbxCredentialHelper").IsVisible, Is.False);
        TextBox helper = Field<TextBox>(dialog, "ReadOnlycbxCredentialHelper");
        Assert.That(helper.IsReadOnly, Is.True); Assert.That(helper.Text, Is.EqualTo(", cache"));
        Assert.That(ToolTip.GetTip(helper)?.ToString(), Does.Contain("multiple").And.Contain(".git/config"));
        Edit(dialog, "GlobalUserEmail", "new@example.invalid"); Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastError, Is.Null);
        Assert.That((await f.Git.GitAsync("config", "--local", "--get-all", "credential.helper")).StandardOutput, Is.EqualTo("\ncache\n"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ExistingValuelessFieldsStayUntouchedAndCanBeExplicitlyUnsetEvenWhenDisplayedEmpty()
    {
        await using Scope f = await Scope.CreateAsync();
        File.AppendAllText(f.Git.PathInRepository(".git/config"), "\n[core]\n\tautocrlf\n[commit]\n\ttemplate\n[credential]\n\thelper\n[i18n]\n\tfilesencoding\n");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Assert.That(Field<ComboBox>(dialog, "cbxCredentialHelper").Text, Is.Empty);
        Assert.That(Field<RadioButton>(dialog, "globalAutoCrlfTrue").IsChecked, Is.True);
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        await ChooseScope(dialog, 0); await ChooseScope(dialog, 1); Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
        await FieldAction(dialog, "txtCommitTemplatePath", "UnsetCommitTemplate");
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.Read("local", "commit.template"), Is.Null);
        Assert.That(dialog.Page.Session!.Scope(GitToolConfigurationView.Local).Setting(GitConfigurationField.CredentialHelper).IsPresent, Is.True);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ReturningToPublishedValuelessEncodingCancelsTheDraftWhileNotSetExplicitlyRemovesIt()
    {
        await using Scope f = await Scope.CreateAsync();
        File.AppendAllText(f.Git.PathInRepository(".git/config"), "\n[i18n]\n\tfilesencoding\n");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Assert.That((Field<ComboBox>(dialog, "Global_FilesEncoding").SelectedItem as GitToolsSettingsPage.EncodingChoice)?.RestorePublished, Is.True);
        ChooseEncoding(dialog, "utf-8"); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.True);
        ChooseEncodingChoice(dialog, choice => choice.RestorePublished);
        Assert.That(dialog.Page.Session!.Scope(GitToolConfigurationView.Local).Setting(GitConfigurationField.FilesEncoding).IsPresent, Is.True);
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        Click(dialog, "GitToolsAccept"); await Until(() => done.IsCompleted); Assert.That(await done, Is.True);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(dialog.MayHaveChangedConfiguration, Is.False);
        (GitToolsSettingsDialog reopened, Task<bool> reopenedDone) = await f.Open(); await ChooseScope(reopened, 1);
        ChooseEncodingChoice(reopened, choice => choice.Value is null && !choice.RestorePublished);
        Click(reopened, "GitToolsApply"); await Idle(reopened); Assert.That(reopened.LastError, Is.Null);
        Assert.That(await f.Read("local", "i18n.filesencoding"), Is.Null);
        Assert.That(reopened.Page.Session!.Scope(GitToolConfigurationView.Local).Setting(GitConfigurationField.FilesEncoding).IsPresent, Is.False);
        Click(reopened, "GitToolsCancel"); Assert.That(await reopenedDone, Is.False);
    }

    [AvaloniaTest]
    public async Task ExplicitEmptyHelperResetAndUnsetAreDifferentAndTextClearsRemoveTheScopedKey()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "credential.helper", "cache");
        await f.Set("local", "commit.template", "/old template.txt");
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        await FieldAction(dialog, "cbxCredentialHelper", "ResetCredentialHelpers");
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.Read("local", "credential.helper"), Is.EqualTo(""));
        await FieldAction(dialog, "cbxCredentialHelper", "UnsetCredentialHelper"); Edit(dialog, "txtCommitTemplatePath", "");
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.Read("local", "credential.helper"), Is.Null); Assert.That(await f.Read("local", "commit.template"), Is.Null);
        Click(dialog, "globalAutoCrlfInput"); Click(dialog, "GitToolsApply"); await Idle(dialog);
        Click(dialog, "globalAutoCrlfNotSet"); Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(await f.Read("local", "core.autocrlf"), Is.Null);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task ValidationSelectsAndFocusesTheExactScalarBeforeAnyNativeWrite()
    {
        await using Scope f = await Scope.CreateAsync(); string before = f.ConfigurationBytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        dialog.Page.Session!.Scope(GitToolConfigurationView.Global).Setting(GitConfigurationField.Editor).SetValue("invalid\0editor");
        await dialog.PublishAsync(closeOnSuccess: false); await Idle(dialog); Pump(dialog);
        Assert.That(dialog.LastError, Does.Contain("NUL")); Assert.That(dialog.Page.View, Is.EqualTo(GitToolConfigurationView.Global));
        Control? focused = dialog.FocusManager?.GetFocusedElement() as Control;
        ComboBox editor = Field<ComboBox>(dialog, "GlobalEditor");
        Assert.That(focused == editor || focused?.GetVisualAncestors().Contains(editor) == true, Is.True);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(dialog.LastSaveResult, Is.Null);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task InvalidPendingLineEndingIntentFocusesItsRadioWithoutPublishingAnyFields()
    {
        await using Scope f = await Scope.CreateAsync(); string before = f.ConfigurationBytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open();
        GitToolsDraftSession.ScopeDraft draft = dialog.Page.Session!.Scope(GitToolConfigurationView.Local);
        draft.Setting(GitConfigurationField.UserName).SetValue("must stay pending");
        draft.Setting(GitConfigurationField.AutoCrlf).SetValue("invalid new line-ending choice");
        await dialog.PublishAsync(closeOnSuccess: false); await Idle(dialog); Pump(dialog);
        Assert.That(dialog.LastError, Does.Contain("line endings")); Assert.That(dialog.Page.View, Is.EqualTo(GitToolConfigurationView.Local));
        Assert.That(dialog.FocusManager?.GetFocusedElement(), Is.SameAs(Field<RadioButton>(dialog, "globalAutoCrlfNotSet")));
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(dialog.LastSaveResult, Is.Null);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task PartialScalarFailureRebasesPublishedFieldsAndReloadRetainsThePendingOtherScope()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Git.GitAsync("config", "--local", "--unset", "user.email");
        await f.Set("global", "user.email", "old-global@example.invalid");
        string marker = f.Git.Output("fail-global-once");
        string wrapper = f.Wrapper("""
            case " $* " in
              *" config --global -- user.email "*)
                if ! test -e @MARKER@; then touch @MARKER@; printf 'controlled scalar failure\n' >&2; exit 73; fi;;
            esac
            exec git "$@"
            """.Replace("@MARKER@", Quote(marker), StringComparison.Ordinal));
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(new(f.Git.Repository.Path, new GitRunner(wrapper)));
        await ChooseScope(dialog, 1); Edit(dialog, "GlobalUserName", "published local name");
        await ChooseScope(dialog, 2); Edit(dialog, "GlobalUserEmail", "pending-global@example.invalid");
        Click(dialog, "GitToolsApply"); await Idle(dialog);
        Assert.That(dialog.LastSaveResult?.Success, Is.False); Assert.That(dialog.LastError, Does.Contain("controlled scalar failure").And.Contain("user.name"));
        Assert.That(await f.Read("local", "user.name"), Is.EqualTo("published local name"));
        Assert.That(await f.Read("global", "user.email"), Is.EqualTo("old-global@example.invalid"));
        Assert.That(dialog.Page.Session!.Scope(GitToolConfigurationView.Local).Setting(GitConfigurationField.UserName).IsDirty, Is.False);
        Assert.That(Field<TextBox>(dialog, "GlobalUserEmail").Text, Is.EqualTo("pending-global@example.invalid"));
        Click(dialog, "GitToolsReload"); await Idle(dialog); Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.True);
        Click(dialog, "GitToolsApply"); await Idle(dialog); Assert.That(dialog.LastError, Is.Null);
        Assert.That(await f.Read("global", "user.email"), Is.EqualTo("pending-global@example.invalid"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
    }

    [AvaloniaTest]
    public async Task SuccessfulScalarWriteMaskedByAnIncludeKeepsPendingIntentAndReportsActualPublishedValue()
    {
        await using Scope f = await Scope.CreateAsync();
        await f.Git.GitAsync("config", "--local", "--unset", "user.name");
        string include = f.Git.Output("later-user.config"); File.WriteAllText(include, "[user]\n\tname = included winning name\n");
        await f.Set("local", "include.path", include);
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Edit(dialog, "GlobalUserName", "native requested name"); Click(dialog, "GitToolsAccept"); await Idle(dialog);
        Assert.That(dialog.LastSaveResult?.Success, Is.True); Assert.That(done.IsCompleted, Is.False);
        Assert.That(dialog.LastError, Does.Contain("included winning name").And.Contain("Published values"));
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("native requested name"));
        await ChooseScope(dialog, 0); Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("included winning name"));
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(await f.Read("local", "user.name"), Is.EqualTo("native requested name"));
    }

    [AvaloniaTest]
    public async Task TemplatePickerCancellationAndLateResultUseTheSameOwnedLifetimeAsToolPickers()
    {
        await using Scope f = await Scope.CreateAsync(); await f.Set("local", "commit.template", "/original template.txt");
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        dialog.TemplateFilePicker = (_, _) => Task.FromResult<string?>(null);
        Click(dialog, "btnCommitTemplateBrowse"); await Idle(dialog);
        Assert.That(Field<TextBox>(dialog, "txtCommitTemplatePath").Text, Is.EqualTo("/original template.txt"));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.False);
        dialog.TemplateFilePicker = (_, _) => Task.FromResult<string?>("/accepted ' template.txt");
        Click(dialog, "btnCommitTemplateBrowse"); await Idle(dialog);
        Assert.That(Field<TextBox>(dialog, "txtCommitTemplatePath").Text, Is.EqualTo("/accepted ' template.txt"));
        TaskCompletionSource<string?> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.TemplateFilePicker = (_, _) => late.Task;
        try
        {
            Click(dialog, "btnCommitTemplateBrowse"); Task operation = dialog.OperationCompletion;
            dialog.RequestClose(); Assert.That(done.IsCompleted, Is.False); late.SetResult("/late ignored.txt");
            await Until(() => done.IsCompleted); await operation; Assert.That(await done, Is.False);
            Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(dialog.MayHaveChangedConfiguration, Is.False);
        }
        finally { late.TrySetResult(null); }
    }

    [AvaloniaTest]
    public async Task RealEncodingChooserIsOwnedAndAnOwnerCloseDrainsItWithoutPublishingTheGitDraft()
    {
        await using Scope f = await Scope.CreateAsync(); Window owner = f.Owner();
        string before = f.ConfigurationBytes(); (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(owner: owner); await ChooseScope(dialog, 1);
        Edit(dialog, "GlobalUserName", "unsaved while choosing encodings"); Click(dialog, "ConfigureEncoding");
        await Until(() => dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Any(window => window.IsVisible));
        AvailableEncodingsDialog chooser = dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Single();
        Assert.That(chooser.Owner, Is.SameAs(dialog)); Assert.That(dialog.IsBusy, Is.True); Task operation = dialog.OperationCompletion;
        owner.Close(); await Until(() => done.IsCompleted && !owner.IsVisible && !chooser.IsVisible); await operation;
        Assert.That(await done, Is.False); Assert.That(f.ConfigurationBytes(), Is.EqualTo(before));
        Assert.That(File.Exists(f.EncodingPreferences.FilePath), Is.False);
    }

    [AvaloniaTest]
    public async Task EncodingChooserCancelPreservesGitDraftAndAcceptedPreferencesSurviveConfigCancel()
    {
        await using Scope f = await Scope.CreateAsync(); string before = f.ConfigurationBytes();
        (GitToolsSettingsDialog dialog, Task<bool> done) = await f.Open(); await ChooseScope(dialog, 1);
        Edit(dialog, "GlobalUserName", "draft while configuring encodings"); Click(dialog, "ConfigureEncoding");
        await Until(() => dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Any(window => window.IsVisible));
        AvailableEncodingsDialog chooser = dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Single();
        Click(chooser, "ButtonCancel"); await Idle(dialog);
        Assert.That(Field<TextBox>(dialog, "GlobalUserName").Text, Is.EqualTo("draft while configuring encodings"));
        Assert.That(File.Exists(f.EncodingPreferences.FilePath), Is.False);
        Click(dialog, "ConfigureEncoding"); await Until(() => dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Any(window => window.IsVisible));
        chooser = dialog.OwnedWindows.OfType<AvailableEncodingsDialog>().Single();
        ListBox available = Field<ListBox>(chooser, "ListAvailableEncodings");
        ClickControl(available); Press(chooser, PhysicalKey.Home); Pump(chooser);
        string added = ((GitFileEncodingChoice)available.SelectedItem!).Name;
        Click(chooser, "ToLeft");
        Click(chooser, "ButtonOk"); await Idle(dialog); Assert.That(File.Exists(f.EncodingPreferences.FilePath), Is.True);
        byte[] publishedPreferences = File.ReadAllBytes(f.EncodingPreferences.FilePath);
        Assert.That(Field<ComboBox>(dialog, "Global_FilesEncoding").Items.OfType<GitToolsSettingsPage.EncodingChoice>().Select(choice => choice.Value), Does.Contain(added));
        Assert.That(Field<Button>(dialog, "GitToolsApply").IsEnabled, Is.True);
        Click(dialog, "GitToolsCancel"); Assert.That(await done, Is.False);
        Assert.That(f.ConfigurationBytes(), Is.EqualTo(before)); Assert.That(File.ReadAllBytes(f.EncodingPreferences.FilePath), Is.EqualTo(publishedPreferences));
    }

    [AvaloniaTest]
    public async Task ScalarChildApplyCancelRetainsParentFontAndScriptDraftsAndReturnsFocusToSettings()
    {
        await using Scope f = await Scope.CreateAsync();
        ScriptsPreferencesStore scripts = new(); scripts.Save([new() { Name = "saved script", Command = "/bin/true", HotkeyCommandIdentifier = 9001 }]);
        byte[] savedScripts = File.ReadAllBytes(scripts.FilePath);
        MainWindow main = new() { Width = 1280, Height = 900 }; f.Own(main); main.Show(); await main.OpenRepositoryAsync(f.Git.Repository.Path);
        await Until(() => !main.IsBusy); Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any()); DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single();
        SettingsNavigationTestDriver.Select(settings, "Scripts"); Edit(settings, "ScriptName", "parent script draft");
        SettingsNavigationTestDriver.Select(settings, "Fonts"); ClickControl(Field<CheckBox>(settings, "ShowEolMarkerAsGlyph"));
        bool eolDraft = Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph;
        SettingsNavigationTestDriver.Select(settings, "Config"); Click(settings, "OpenGitToolsConfiguration");
        await Until(() => settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Any()); GitToolsSettingsDialog child = settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Single();
        await child.InitializeAsync(); await Idle(child); await ChooseScope(child, 1); Edit(child, "GlobalUserEmail", "child-applied@example.invalid");
        Click(child, "GitToolsApply"); await Idle(child); Assert.That(child.LastError, Is.Null); Click(child, "GitToolsCancel");
        await Until(() => !child.IsVisible && !main.IsBusy && Field<Button>(settings, "OpenGitToolsConfiguration").IsEnabled);
        Control? focused = settings.FocusManager?.GetFocusedElement() as Control;
        Assert.That(focused is not null && TopLevel.GetTopLevel(focused) == settings, Is.True);
        SettingsNavigationTestDriver.Select(settings, "Scripts"); Assert.That(Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage").Draft[0].Name, Is.EqualTo("parent script draft"));
        SettingsNavigationTestDriver.Select(settings, "Fonts"); Assert.That(Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph, Is.EqualTo(eolDraft));
        Click(settings, "CancelDesktopSettings"); await Until(() => !settings.IsVisible && !main.IsBusy);
        Assert.That(File.ReadAllBytes(scripts.FilePath), Is.EqualTo(savedScripts));
        Assert.That(await f.Read("local", "user.email"), Is.EqualTo("child-applied@example.invalid"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task MainSettingsAdvancedRouteAppliesNativeBooleanWhileParentDraftsAndCancelRemainIndependent(bool outsideRepository)
    {
        await using Scope f = await Scope.CreateAsync();
        new PreferencesStore().Save(new() { Language = "English", StartWithRecentWorkingDir = false });
        ScriptsPreferencesStore scripts = new(); scripts.Save([new() { Name = "saved script", Command = "/bin/true", HotkeyCommandIdentifier = 9001 }]);
        byte[] savedScripts = File.ReadAllBytes(scripts.FilePath);
        FontPreferencesStore fonts = new(); byte[]? savedFonts = File.Exists(fonts.FilePath) ? File.ReadAllBytes(fonts.FilePath) : null;
        byte[] originalLocal = File.ReadAllBytes(f.Git.PathInRepository(".git/config")), originalGlobal = File.ReadAllBytes(f.Git.Output("global.config"));
        Assert.That(await f.Read(outsideRepository ? "global" : "local", "fetch.prune"), Is.Null);
        MainWindow main = new() { Width = 1280, Height = 900 }; f.Own(main); main.Show();
        if (!outsideRepository) { await main.OpenRepositoryAsync(f.Git.Repository.Path); }
        await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        await Until(() => !main.IsBusy); Press(main, PhysicalKey.Comma, RawInputModifiers.Control);
        await Until(() => main.OwnedWindows.OfType<DesktopSettingsDialog>().Any(settings => settings.IsVisible));
        DesktopSettingsDialog settings = main.OwnedWindows.OfType<DesktopSettingsDialog>().Single(window => window.IsVisible);
        SettingsNavigationTestDriver.Select(settings, "Scripts"); Edit(settings, "ScriptName", "pending parent script after Advanced");
        SettingsNavigationTestDriver.Select(settings, "Fonts"); ClickControl(Field<CheckBox>(settings, "ShowEolMarkerAsGlyph"));
        bool eolDraft = Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph;
        SettingsNavigationTestDriver.Select(settings, "GitAdvanced"); Click(settings, "OpenGitAdvancedConfiguration");
        await Until(() => settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Any(window => window.IsVisible));
        GitToolsSettingsDialog child = settings.OwnedWindows.OfType<GitToolsSettingsDialog>().Single(window => window.IsVisible);
        await child.InitializeAsync(); await Idle(child); Pump(child);
        Assert.That(child.Owner, Is.SameAs(settings)); Assert.That(child.IsReady, Is.True, child.LastError);
        Assert.That(Field<TabControl>(child, "GitConfigPages").SelectedItem, Is.SameAs(Field<TabItem>(child, "GitAdvancedTab")));
        Assert.That(child.AdvancedPage.IsEffectivelyVisible, Is.True); Assert.That(TopLevel.GetTopLevel(child.AdvancedPage), Is.SameAs(child));
        Assert.That(child.GetVisualDescendants(), Does.Not.Contain(child.Page), "The unselected Config page must not occur in the rendered window tree.");
        Assert.That(child.AdvancedPage.Session, Is.SameAs(child.Page.Session));
        ComboBox scope = Field<ComboBox>(child, "GitAdvancedScope");
        if (outsideRepository)
        {
            Assert.That(child.AdvancedPage.View, Is.EqualTo(GitToolConfigurationView.Global));
            Assert.That(child.AdvancedPage.Session!.Snapshot.GetScope(GitToolConfigurationView.Local).IsAvailable, Is.False);
            Choose(child, scope, 1); Assert.That(Field<CheckBox>(child, "checkBoxFetchPrune").IsEnabled, Is.False);
            Choose(child, scope, 2);
        }
        else { Choose(child, scope, 1); }
        CheckBox prune = Field<CheckBox>(child, "checkBoxFetchPrune");
        Assert.That(child.AdvancedPage.View, Is.EqualTo(outsideRepository ? GitToolConfigurationView.Global : GitToolConfigurationView.Local));
        Assert.That(prune.IsEffectivelyEnabled, Is.True); Assert.That(prune.IsChecked, Is.Null);
        ClickControl(prune);
        for (int cycle = 0; cycle < 3 && prune.IsChecked != true; cycle++) { Press(child, PhysicalKey.Space); }
        Assert.That(prune.IsChecked, Is.True); Assert.That(prune.IsKeyboardFocusWithin, Is.True);
        Click(child, "GitToolsApply"); await Idle(child);
        Assert.That(child.LastError, Is.Null); Assert.That(child.LastSaveResult?.Success, Is.True);
        GitToolConfigurationWriteReceipt receipt = child.LastSaveResult!.Writes.Single();
        Assert.That(receipt.Key, Is.EqualTo("fetch.prune")); Assert.That(receipt.RequestedValue, Is.EqualTo("true"));
        Assert.That(receipt.Scope, Is.EqualTo(outsideRepository ? GitConfigScope.Global : GitConfigScope.Local));
        Assert.That(receipt.Outcome, Is.EqualTo(GitToolConfigurationWriteOutcome.NativeCompleted)); Assert.That(receipt.NativeResult?.Success, Is.True);
        Assert.That(child.AdvancedPage.Session, Is.SameAs(child.Page.Session)); Assert.That(Field<Button>(child, "GitToolsApply").IsEnabled, Is.False);
        Click(child, "GitToolsCancel");
        await Until(() => !child.IsVisible && !main.IsBusy && Field<Button>(settings, "OpenGitAdvancedConfiguration").IsEnabled);
        Control? focused = settings.FocusManager?.GetFocusedElement() as Control;
        Assert.That(focused is not null && TopLevel.GetTopLevel(focused) == settings, Is.True);
        SettingsNavigationTestDriver.Select(settings, "Scripts");
        Assert.That(Field<ScriptsSettingsPage>(settings, "ScriptsSettingsPage").Draft[0].Name, Is.EqualTo("pending parent script after Advanced"));
        SettingsNavigationTestDriver.Select(settings, "Fonts");
        Assert.That(Field<AppearanceFontsSettingsPage>(settings, "AppearanceFontsSettingsPage").Draft.ShowEolMarkerAsGlyph, Is.EqualTo(eolDraft));
        Click(settings, "CancelDesktopSettings"); await Until(() => !settings.IsVisible && !main.IsBusy);
        Assert.That(await f.Read(outsideRepository ? "global" : "local", "fetch.prune"), Is.EqualTo("true"), "Parent Cancel must retain the child's already published native Git value.");
        Assert.That(File.ReadAllBytes(scripts.FilePath), Is.EqualTo(savedScripts));
        if (savedFonts is null) { Assert.That(File.Exists(fonts.FilePath), Is.False); }
        else { Assert.That(File.ReadAllBytes(fonts.FilePath), Is.EqualTo(savedFonts)); }
        Assert.That(File.ReadAllBytes(outsideRepository ? f.Git.PathInRepository(".git/config") : f.Git.Output("global.config")), Is.EqualTo(outsideRepository ? originalLocal : originalGlobal), "The other native scope is byte-identical.");
        await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Scope(ReferenceFixture git) : IAsyncDisposable
    {
        private readonly List<Window> _windows = [];
        internal ReferenceFixture Git => git;
        internal AvailableEncodingsPreferencesStore EncodingPreferences { get; } = new(git.Output("encoding-preferences"));
        internal static async Task<Scope> CreateAsync()
        {
            ReferenceFixture git = await ReferenceFixture.CreateAsync();
            try
            {
                Directory.CreateDirectory(git.Output("home")); git.SetEnvironment("HOME", git.Output("home"));
                git.SetEnvironment("GIT_CONFIG_GLOBAL", git.Output("global.config")); File.WriteAllText(git.Output("global.config"), "");
                git.SetEnvironment("GIT_CONFIG_SYSTEM", git.Output("system.config")); File.WriteAllText(git.Output("system.config"), "");
                git.SetEnvironment("GIT_CONFIG_NOSYSTEM", null); git.SetEnvironment("GIT_CONFIG_COUNT", null);
                git.SetEnvironment("GIT_CONFIG_PARAMETERS", null); git.SetEnvironment("GIT_CONFIG", null);
                foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToArray())
                { git.SetEnvironment(name, null); }
                return new(git);
            }
            catch { git.Dispose(); throw; }
        }
        internal void Own(Window window) => _windows.Add(window);
        internal Window Owner(bool dark = false)
        {
            Window window = new() { Width = 1100, Height = 800, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Content = new TextBox { Text = "Config owner" } };
            Own(window); window.Show(); return window;
        }
        internal async Task<(GitToolsSettingsDialog Dialog, Task<bool> Done)> Open(GitRepository? repository = null, Window? owner = null, bool dark = false, bool allowUnavailable = false)
        {
            owner ??= Owner(dark); GitToolsSettingsDialog dialog = new(repository ?? git.Repository, encodingPreferences: EncodingPreferences); Own(dialog);
            Task<bool> done = dialog.ShowDialog<bool>(owner); await dialog.InitializeAsync(); await Idle(dialog);
            if (!allowUnavailable) { Assert.That(dialog.IsReady, Is.True, dialog.LastError); Assert.That(dialog.LastError, Is.Null); }
            return (dialog, done);
        }
        internal Task<GitResult> Set(string scope, string key, string value) => git.GitAsync("config", "--" + scope, key, value);
        internal async Task<string?> Read(string scope, string key)
        {
            GitResult result = await new GitRunner().RunAsync(git.Repository.Path, ["config", "--" + scope, "--get", key]);
            Assert.That(result.ExitCode, Is.AnyOf(0, 1), result.StandardError); return result.ExitCode == 1 ? null : result.StandardOutput[..^1];
        }
        internal string ConfigurationBytes() => string.Join("|", new[] { git.PathInRepository(".git/config"), git.Output("global.config"), git.Output("system.config") }.Select(path => Convert.ToHexString(File.ReadAllBytes(path))));
        internal string Wrapper(string body)
        {
            string file = git.Output("git-wrapper-" + Guid.NewGuid().ToString("N")); File.WriteAllText(file, "#!/bin/sh\nset -eu\n" + body + "\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } return file;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (Window window in _windows.AsEnumerable().Reverse())
                {
                    foreach (Window child in PushProcessTestDriver.Windows(window).Reverse().ToArray()) { if (child.IsVisible) { child.Close(); } }
                    if (window.IsVisible) { window.Close(); }
                    if (window is GitToolsSettingsDialog config) { await config.OperationCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    if (window is MainWindow main) { await main.WindowWorkCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
                    await Until(() => !window.IsVisible);
                }
            }
            finally { git.Dispose(); }
        }
    }

    private static async Task FieldAction(Window window, string controlName, string actionName)
    {
        Control input = Field<Control>(window, controlName); input.BringIntoView(); Pump(window);
        Assert.That(input.ContextMenu, Is.Not.Null); input.ContextMenu!.Open(input); await Task.Yield(); Pump(window);
        MenuItem item = input.ContextMenu.Items.OfType<MenuItem>().Single(value => value.Name == actionName);
        ClickControl(item); Pump(window); Assert.That(input.ContextMenu.IsOpen, Is.False);
    }
    private static async Task ChooseScope(GitToolsSettingsDialog dialog, int index)
    {
        ComboBox combo = Field<ComboBox>(dialog, "GitToolsScope"); Choose(dialog, combo, index); await Task.Yield(); Pump(dialog);
        Assert.That(combo.SelectedIndex, Is.EqualTo(index));
    }
    private static void ChooseEncoding(Window window, string name)
    {
        ChooseEncodingChoice(window, choice => choice.Value == name); Assert.That(SelectedEncoding(window), Is.EqualTo(name));
    }
    private static void ChooseEncodingChoice(Window window, Func<GitToolsSettingsPage.EncodingChoice, bool> predicate)
    {
        ComboBox combo = Field<ComboBox>(window, "Global_FilesEncoding");
        int index = combo.Items.OfType<GitToolsSettingsPage.EncodingChoice>().ToList().FindIndex(choice => predicate(choice));
        Assert.That(index, Is.GreaterThanOrEqualTo(0)); Choose(window, combo, index);
    }
    private static string? SelectedEncoding(Window window) => (Field<ComboBox>(window, "Global_FilesEncoding").SelectedItem as GitToolsSettingsPage.EncodingChoice)?.Value;
    private static void Choose(Window window, ComboBox combo, int index)
    {
        ClickControl(combo); Assert.That(combo.IsDropDownOpen, Is.True); Press(window, PhysicalKey.Home);
        for (int i = 0; i < index; i++) { Press(window, PhysicalKey.ArrowDown); }
        Press(window, PhysicalKey.Enter); Pump(window); Assert.That(combo.SelectedIndex, Is.EqualTo(index));
    }
    private static void EditCombo(Window window, string name, string value)
    {
        ComboBox combo = Field<ComboBox>(window, name); combo.BringIntoView(); Pump(window);
        TextBox editor = combo.GetVisualDescendants().OfType<TextBox>().First(); EditControl(window, editor, value);
        Assert.That(combo.Text, Is.EqualTo(value)); Press(window, PhysicalKey.Tab); Pump(window);
    }
    private static void Edit(Window window, string name, string value) => EditControl(window, Field<TextBox>(window, name), value);
    private static void EditControl(Window window, TextBox input, string value)
    {
        ClickControl(input); Press(window, PhysicalKey.A, RawInputModifiers.Control);
        if (value.Length == 0) { Press(window, PhysicalKey.Backspace); } else { window.KeyTextInput(value); }
        Pump(window); Assert.That(input.Text, Is.EqualTo(value));
    }
    private static void Click(Window window, string name) => ClickControl(Field<Button>(window, name));
    private static void ClickControl(Control input) { input.BringIntoView(); PushProcessTestDriver.Pointer(input); }
    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static Task Idle(GitToolsSettingsDialog dialog) => Until(() => !dialog.IsBusy);
    private static async Task Until(Func<bool> complete)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.That(complete(), Is.True, "The real configuration operation or owned window did not settle.");
    }
    private static void Pump(TopLevel window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Dispatcher.UIThread.RunJobs(); }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void AssertOriginalColumnsAligned(Window window)
    {
        Pump(window); Assert.That(window.ClientSize.Width, Is.EqualTo(820));
        string[] inputs = ["GlobalUserName", "GlobalUserEmail", "cbxCredentialHelper", "GlobalEditor", "_NO_TRANSLATE_cboMergeTool", "txtMergeToolPath", "txtMergeToolCommand", "_NO_TRANSLATE_cboDiffTool", "txtDiffToolPath", "txtDiffToolCommand", "txtCommitTemplatePath", "Global_FilesEncoding"];
        double expected = Field<Control>(window, inputs[0]).TranslatePoint(default, window)!.Value.X;
        foreach (string name in inputs)
        { Assert.That(Field<Control>(window, name).TranslatePoint(default, window)!.Value.X, Is.EqualTo(expected).Within(0.5), name + " must use the original shared input column."); }
        string[] actions = ["btnMergeToolBrowse", "btnMergeToolCommandSuggest", "btnDiffToolBrowse", "btnDiffToolCommandSuggest", "btnCommitTemplateBrowse", "ConfigureEncoding"];
        expected = Field<Control>(window, actions[0]).TranslatePoint(default, window)!.Value.X;
        foreach (string name in actions)
        { Assert.That(Field<Control>(window, name).TranslatePoint(default, window)!.Value.X, Is.EqualTo(expected).Within(0.5), name + " must use the original shared action column."); }
    }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame!.Save(path); TestContext.AddTestAttachment(path);
    }
}
