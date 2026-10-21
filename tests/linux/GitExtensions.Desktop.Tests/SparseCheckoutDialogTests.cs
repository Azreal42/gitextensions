using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class SparseCheckoutDialogTests
{
    [AvaloniaTest]
    [TestCase(true)] [TestCase(false)]
    public async Task SaveWithoutRefreshClosesAndPersistsRulesButDefersEveryCheckoutChange(bool cone)
    {
        using Fixture f = await CreateAsync();
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            Assert.That(Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked, Is.True);
            Edit(dialog, cone); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            Pointer(dialog, "SaveSparse"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges, Is.True);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True);
            GitSparseCheckoutState saved = await f.Repository.GetSparseCheckoutAsync();
            Assert.That(saved.Enabled && saved.ConeMode == cone && saved.SparseIndex == cone, Is.True);
            Assert.That(saved.Patterns, Is.EqualTo(cone ? new[] { "included" } : new[] { "/included/**" }));
            dialog = await OpenAsync(f.Repository);
            Assert.That(Field<TextBox>(dialog, "SparseRules").Text, Is.EqualTo(cone ? "included" : "/included/**"));
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True, "Reopening saved settings must not imply or perform a refresh.");
            Pointer(dialog, "ReapplySparse"); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.IsVisible, Is.True, "The separate Refresh action remains available without closing the editor.");
            Assert.That(File.Exists(f.PathInRepository("included/file.txt")), Is.True);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task DefaultSaveAndCtrlEnterRefreshThenClose(bool keyboard)
    {
        using Fixture f = await CreateAsync(); SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        using SparseKeyTrace? trace = keyboard ? new(dialog, Field<TextBox>(dialog, "SparseRules")) : null;
        try
        {
            Edit(dialog, true);
            if (keyboard)
            {
                trace!.Record("before-existing-focus");
                Field<TextBox>(dialog, "SparseRules").Focus();
                trace!.Record("before-first-ctrl-enter");
                dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
                trace!.Record("after-first-keypress");
                if (dialog.IsVisible && dialog.PlatformImpl is not null)
                { dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control); }
                trace!.Record("after-keyrelease-or-closed");
            }
            else { Pointer(dialog, "SaveSparse"); }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(File.Exists(f.PathInRepository("included/file.txt")), Is.True);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.False);
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).SparseIndex, Is.True);
        }
        catch (Exception error) when (keyboard) { trace!.CaptureFailure(error); throw; }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task RefreshCheckboxAloneDoesNotMakeTheEditorDirtyOrWriteOnClose()
    {
        using Fixture f = await CreateAsync(); SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        byte[] config = await File.ReadAllBytesAsync(f.PathInRepository(".git/config"));
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        try
        {
            Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false; dialog.Close();
            await Until(() => !dialog.IsVisible);
            Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(dialog.MayHaveRepositoryChanges, Is.False);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config")), Is.EqualTo(config));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(File.Exists(f.PathInRepository(".git/info/sparse-checkout")), Is.False);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SavingAnUnchangedEditorStillRefreshesThePersistedSettingsByDefault()
    {
        using Fixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"]);
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            // As in Windows, the settings may have been edited outside an unchanged editor.
            await f.Repository.SaveSparseCheckoutSettingsAsync(true, true, false, ["excluded"]);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.False);
            Pointer(dialog, "SaveSparse"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True);
            Assert.That(File.Exists(f.PathInRepository("included/file.txt")), Is.False);
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Patterns, Is.EqualTo(new[] { "excluded" }));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase("SparseEnabled")] [TestCase("SparseCone")] [TestCase("SparseIndex")] [TestCase("SparseRules")]
    public async Task EveryEditableSettingParticipatesInTheDirtyDecision(string field)
    {
        using Fixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"], sparseIndex: true);
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        byte[] config = await File.ReadAllBytesAsync(f.PathInRepository(".git/config.worktree"));
        byte[] rules = await File.ReadAllBytesAsync(f.PathInRepository(".git/info/sparse-checkout"));
        try
        {
            if (field == "SparseRules") { Field<TextBox>(dialog, field).Text = "excluded"; }
            else { Field<CheckBox>(dialog, field).IsChecked = false; }
            dialog.Close(); Pointer(await QuestionAsync(dialog), "SparseSaveNo"); await Until(() => !dialog.IsVisible);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config.worktree")), Is.EqualTo(config));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/info/sparse-checkout")), Is.EqualTo(rules));
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase("Close", "Yes")] [TestCase("Close", "No")] [TestCase("Close", "Cancel")]
    [TestCase("Cancel", "Yes")] [TestCase("Cancel", "No")] [TestCase("Cancel", "Cancel")]
    [TestCase("Escape", "Yes")] [TestCase("Escape", "No")] [TestCase("Escape", "Cancel")]
    public async Task DirtyCloseUsesTheOriginalYesNoCancelContract(string trigger, string answer)
    {
        using Fixture f = await CreateAsync(); SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        byte[] config = await File.ReadAllBytesAsync(f.PathInRepository(".git/config"));
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        try
        {
            Edit(dialog, true); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            if (trigger == "Close") { dialog.Close(); }
            else if (trigger == "Cancel") { Pointer(dialog, "CancelSparse"); }
            else
            {
                Field<TextBox>(dialog, "SparseRules").Focus();
                dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            }
            Window question = await QuestionAsync(dialog);
            Assert.That(Field<Button>(question, "SparseSaveYes").IsDefault, Is.True);
            Assert.That(Field<Button>(question, "SparseSaveCancel").IsCancel, Is.True);
            Pointer(question, "SparseSave" + answer);
            await Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
            if (answer == "Cancel")
            {
                Assert.That(dialog.IsVisible, Is.True);
                Assert.That(Field<TextBox>(dialog, "SparseRules").Text, Is.EqualTo("included\n"));
                Assert.That(Field<CheckBox>(dialog, "SparseEnabled").IsChecked, Is.True);
                Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config")), Is.EqualTo(config));
                dialog.Close(); question = await QuestionAsync(dialog); Pointer(question, "SparseSaveNo");
            }
            await Until(() => !dialog.IsVisible);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.EqualTo(answer == "Yes"));
            if (answer != "Yes") { Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config")), Is.EqualTo(config)); }
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True, "Yes obeys the unchecked Refresh option; No and Cancel do not write.");
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task FailedSaveKeepsTheDraftAndCanBeRetriedWithoutClosing(bool fromCloseQuestion)
    {
        using Fixture f = await CreateAsync(); SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        string rulesPath = f.PathInRepository(".git/info/sparse-checkout");
        Directory.CreateDirectory(Path.GetDirectoryName(rulesPath)!); await File.WriteAllTextAsync(rulesPath + ".lock", "another writer\n");
        byte[] config = await File.ReadAllBytesAsync(f.PathInRepository(".git/config"));
        try
        {
            Edit(dialog, true); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            if (fromCloseQuestion) { dialog.Close(); Pointer(await QuestionAsync(dialog), "SparseSaveYes"); }
            else { Pointer(dialog, "SaveSparse"); }
            await Until(() => !dialog.IsBusy && dialog.LastError is not null);
            Assert.That(dialog.IsVisible, Is.True); Assert.That(dialog.OwnedWindows, Is.Empty);
            Assert.That(Field<TextBox>(dialog, "SparseRules").Text, Is.EqualTo("included\n"));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config")), Is.EqualTo(config));
            Assert.That(File.ReadAllText(rulesPath + ".lock"), Is.EqualTo("another writer\n"));
            File.Delete(rulesPath + ".lock");
            Pointer(dialog, "SaveSparse"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.True);
        }
        finally { File.Delete(rulesPath + ".lock"); await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SavingDisabledWithoutRefreshCanRestoreTheFullCheckoutFromTheReopenedEditor()
    {
        using Fixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"], sparseIndex: true);
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            Field<CheckBox>(dialog, "SparseEnabled").IsChecked = false;
            Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            Pointer(dialog, "SaveSparse"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.False);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.False, "Disabled settings alone do not prove restoration.");
            dialog = await OpenAsync(f.Repository);
            Assert.That(Field<TextBox>(dialog, "SparseRules").Text, Is.EqualTo("included"));
            Assert.That(Field<Button>(dialog, "ReapplySparse").IsEnabled, Is.True);
            Pointer(dialog, "ReapplySparse"); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True);
            dialog.Close(); await Until(() => !dialog.IsVisible);
            Assert.That(dialog.OwnedWindows, Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task DisablingWithImmediateRefreshKeepsTheExistingRestoreConfirmationAndCanBeCancelled()
    {
        using Fixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"], sparseIndex: true);
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        byte[] config = await File.ReadAllBytesAsync(f.PathInRepository(".git/config.worktree"));
        try
        {
            Field<CheckBox>(dialog, "SparseEnabled").IsChecked = false;
            Pointer(dialog, "SaveSparse"); await Until(() => dialog.OwnedWindows.Any());
            Window confirmation = dialog.OwnedWindows.Single();
            Assert.That(confirmation.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Restore the full working copy for this repository?"), Is.True);
            PushProcessTestDriver.Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel));
            await Until(() => dialog.OwnedWindows.Count == 0);
            Assert.That(dialog.IsVisible, Is.True);
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.True);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/config.worktree")), Is.EqualTo(config));
            Pointer(dialog, "SaveSparse"); await Until(() => dialog.OwnedWindows.Any());
            confirmation = dialog.OwnedWindows.Single();
            PushProcessTestDriver.Pointer(confirmation.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel));
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.False);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task RefreshUsesSavedRulesWithoutSilentlySavingTheCurrentDraft()
    {
        using Fixture f = await CreateAsync(); await f.Repository.SetSparseCheckoutAsync(["included"]);
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            Field<TextBox>(dialog, "SparseRules").Text = "excluded\n";
            Pointer(dialog, "ReapplySparse"); await Until(() => !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Patterns, Is.EqualTo(new[] { "included" }));
            Assert.That(File.Exists(f.PathInRepository("included/file.txt")), Is.True);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.False);
            dialog.Close(); Pointer(await QuestionAsync(dialog), "SparseSaveNo"); await Until(() => !dialog.IsVisible);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    public async Task SavingAnEmptyDisabledRuleSetRetainsTheChosenNonConeModeOnReopen()
    {
        using Fixture f = await CreateAsync(); SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            Field<CheckBox>(dialog, "SparseEnabled").IsChecked = true;
            Field<CheckBox>(dialog, "SparseCone").IsChecked = false;
            Field<TextBox>(dialog, "SparseRules").Text = "";
            Field<CheckBox>(dialog, "SparseEnabled").IsChecked = false;
            Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            Pointer(dialog, "SaveSparse"); await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            GitSparseCheckoutState state = await f.Repository.GetSparseCheckoutAsync();
            Assert.That(state.RulesFileExists && !state.Enabled && !state.ConeMode, Is.True);
            Assert.That(state.Patterns, Is.Empty);
            dialog = await OpenAsync(f.Repository);
            Assert.That(Field<CheckBox>(dialog, "SparseCone").IsChecked, Is.False, "An intentionally saved empty non-cone editor is not a fresh unconfigured checkout.");
            dialog.Close(); await Until(() => !dialog.IsVisible);
            Assert.That(dialog.OwnedWindows, Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task SaveQuestionAcceptsTheFirstDefaultOrEscapeKeyWithoutRepairingFocus(bool escape)
    {
        using Fixture f = await CreateAsync(); using DispatcherErrors errors = new();
        SparseCheckoutDialog dialog = await OpenAsync(f.Repository);
        try
        {
            Edit(dialog, true); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            dialog.Close(); Window question = await QuestionAsync(dialog);
            await Until(() => Field<Button>(question, "SparseSaveYes").IsKeyboardFocusWithin);
            Assert.That(question.CanMinimize || question.CanMaximize, Is.False);
            Assert.That(question.Owner, Is.SameAs(dialog));
            // No Focus/Activate call in the test: the first real input must belong to the mapped question.
            Press(question, escape ? PhysicalKey.Escape : PhysicalKey.Enter);
            if (escape)
            {
                await Until(() => !question.IsVisible && dialog.OwnedWindows.Count == 0);
                Assert.That(dialog.IsVisible, Is.True); Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.False);
                dialog.Close(); Pointer(await QuestionAsync(dialog), "SparseSaveNo");
            }
            await Until(() => !dialog.IsVisible && !dialog.IsBusy);
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.EqualTo(!escape));
            Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(errors.Seen, Is.Empty);
        }
        finally { await CloseAsync(dialog); }
    }

    [AvaloniaTest]
    [TestCase("Yes")] [TestCase("No")] [TestCase("Cancel")] [TestCase("failed-save")]
    public async Task OwnerCloseDuringTheQuestionIsRetainedOnlyUntilAnAcceptedClose(string answer)
    {
        using Fixture f = await CreateAsync(); using DispatcherErrors errors = new();
        Window owner = new() { Width = 900, Height = 650 }; owner.Show();
        SparseCheckoutDialog dialog = new(f.Repository); Task modal = dialog.ShowDialog(owner);
        string lockPath = f.PathInRepository(".git/info/sparse-checkout.lock");
        try
        {
            await dialog.InitializeAsync(); Edit(dialog, true); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            dialog.Close(); Window question = await QuestionAsync(dialog);
            await Until(() => Field<Button>(question, "SparseSaveYes").IsKeyboardFocusWithin);
            owner.Close(); Pump(dialog);
            Assert.That(owner.IsVisible && dialog.IsVisible && question.IsVisible, Is.True);
            Assert.That(modal.IsCompleted, Is.False);
            Assert.That(dialog.OwnedWindows.Single(), Is.SameAs(question), "Owner-close must not create a second save question.");
            Assert.That(Field<Button>(question, "SparseSaveYes").IsKeyboardFocusWithin, Is.True);
            if (answer == "failed-save")
            { Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!); await File.WriteAllTextAsync(lockPath, "foreign writer\n"); }
            if (answer == "Cancel") { Press(question, PhysicalKey.Escape); }
            else { Pointer(question, answer == "No" ? "SparseSaveNo" : "SparseSaveYes"); }
            if (answer is "Cancel" or "failed-save")
            {
                await Until(() => !dialog.IsBusy && dialog.OwnedWindows.Count == 0);
                Assert.That(owner.IsVisible && dialog.IsVisible, Is.True);
                Assert.That(modal.IsCompleted, Is.False);
                if (answer == "failed-save") { Assert.That(dialog.LastError, Is.Not.Null); File.Delete(lockPath); }
                Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.False);
                // This is a new, independent child close. Cancel/failed Save revoked the old owner intention.
                dialog.Close(); Pointer(await QuestionAsync(dialog), "SparseSaveNo");
                await Until(() => !dialog.IsVisible && modal.IsCompleted); await modal;
                Pump(owner); await Task.Yield(); Pump(owner);
                Assert.That(owner.IsVisible, Is.True, "A later independent child close must not replay a cancelled owner-close request.");
            }
            else
            {
                await Until(() => !dialog.IsVisible && modal.IsCompleted && !owner.IsVisible); await modal;
                Assert.That((await f.Repository.GetSparseCheckoutAsync()).Enabled, Is.EqualTo(answer == "Yes"));
                Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            }
            Assert.That(question.IsVisible, Is.False); Assert.That(owner.OwnedWindows, Is.Empty);
            Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(errors.Seen, Is.Empty);
        }
        finally
        {
            File.Delete(lockPath); await CloseAsync(dialog); await modal.WaitAsync(TimeSpan.FromSeconds(5));
            if (owner.IsVisible) { owner.Close(); }
        }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task OwnerCloseDuringAnAdmittedSaveOnlyWriteWaitsForTheRealWriterAndClosesWithoutAnotherQuestion(bool fromOwnerQuestion)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Ignore("The bounded real writer gate uses Linux procfs."); return; }
        using Fixture f = await CreateAsync(); using DispatcherErrors errors = new(); using SparseWriteGate gate = new(f);
        GitRepository repository = new(f.Repository.Path, new GitRunner(gate.Executable));
        byte[] index = await File.ReadAllBytesAsync(f.PathInRepository(".git/index"));
        Window owner = new() { Width = 900, Height = 650 }; owner.Show();
        SparseCheckoutDialog dialog = new(repository); Task modal = dialog.ShowDialog(owner);
        try
        {
            await dialog.InitializeAsync(); Edit(dialog, true); Field<CheckBox>(dialog, "SparseRefreshOnSave").IsChecked = false;
            if (fromOwnerQuestion)
            {
                owner.Close(); Window question = await QuestionAsync(dialog);
                await Until(() => Field<Button>(question, "SparseSaveYes").IsKeyboardFocusWithin);
                Press(question, PhysicalKey.Enter);
            }
            else { Pointer(dialog, "SaveSparse"); }
            await Until(() => gate.Started);
            Assert.That(dialog.IsBusy, Is.True); Assert.That(gate.Retired, Is.False);
            Assert.That((await f.GitAsync("config", "--worktree", "--type=bool", "--get", "core.sparseCheckout")).StandardOutput, Is.EqualTo("true\n"),
                "The private writer publishes readiness only after the first real configuration effect.");
            Assert.That(File.Exists(f.PathInRepository(".git/info/sparse-checkout.lock")), Is.True);
            Assert.That(File.Exists(f.PathInRepository(".git/info/sparse-checkout")), Is.False, "The admitted write has not committed its rules yet.");
            owner.Close(); Pump(dialog);
            Assert.That(owner.IsVisible && dialog.IsVisible && dialog.IsBusy, Is.True);
            Assert.That(modal.IsCompleted, Is.False); Assert.That(gate.Retired, Is.False);
            Assert.That(dialog.OwnedWindows, Is.Empty, "Closing during an admitted write must wait rather than open a concurrent save question.");
            gate.Release();
            await Until(() => modal.IsCompleted && !dialog.IsBusy && !dialog.IsVisible && !owner.IsVisible);
            await modal; await Until(() => gate.Retired);
            Pump(owner); await Task.Yield(); Pump(owner);
            Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(dialog.HasRepositoryChanges && dialog.MayHaveRepositoryChanges, Is.True);
            Assert.That((await f.Repository.GetSparseCheckoutAsync()).Patterns, Is.EqualTo(new[] { "included" }));
            Assert.That(File.Exists(f.PathInRepository(".git/info/sparse-checkout.lock")), Is.False);
            Assert.That(File.Exists(f.PathInRepository("excluded/file.txt")), Is.True);
            Assert.That(await File.ReadAllBytesAsync(f.PathInRepository(".git/index")), Is.EqualTo(index));
            Assert.That(owner.OwnedWindows, Is.Empty); Assert.That(dialog.OwnedWindows, Is.Empty); Assert.That(errors.Seen, Is.Empty);
        }
        finally
        {
            gate.Release(); await Until(() => !dialog.IsBusy); await CloseAsync(dialog);
            await modal.WaitAsync(TimeSpan.FromSeconds(5)); if (owner.IsVisible) { owner.Close(); }
            if (gate.Started) { await Until(() => gate.Retired); }
        }
    }

    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task MinimumWindowKeepsTheTranslatedRefreshOptionAndActionsVisible(string language, bool dark)
    {
        using Fixture f = await CreateAsync(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        SparseCheckoutDialog dialog = new(f.Repository) { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        dialog.Show();
        try
        {
            await dialog.InitializeAsync(); dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; Pump(dialog);
            Assert.That(dialog.ClientSize.Width, Is.EqualTo(600).Within(.5)); Assert.That(dialog.ClientSize.Height, Is.EqualTo(420).Within(.5));
            TextBlock caption = Field<TextBlock>(dialog, "SparseRefreshLabel");
            Assert.That(caption.Text, Is.EqualTo(TranslationCatalog.LoadDefault().Translate("Refresh working copy using the current settings and rules")));
            Assert.That(caption.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(caption.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False);
            Assert.That(caption.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(caption.Bounds.Width + .5));
            Assert.That(caption.TextLayout.Height, Is.LessThanOrEqualTo(caption.Bounds.Height + .5));
            foreach (Control control in new Control[] { caption, Field<CheckBox>(dialog, "SparseRefreshOnSave"),
                Field<Button>(dialog, "ReapplySparse"), Field<Button>(dialog, "SaveSparse"), Field<Button>(dialog, "CancelSparse") })
            {
                Assert.That(control.IsEffectivelyVisible, Is.True, control.Name);
                Rect bounds = new(control.TranslatePoint(default, dialog)!.Value, control.Bounds.Size);
                Assert.That(bounds.Width > 0 && bounds.Height > 0 && new Rect(default, dialog.ClientSize).Inflate(.5).Contains(bounds), Is.True, control.Name);
            }
            Capture(dialog, "sparse-minimum-" + language + (dark ? "-dark" : "-light"));
        }
        finally { await CloseAsync(dialog); }
    }

    private sealed class DispatcherErrors : IDisposable
    {
        internal List<Exception> Seen { get; } = [];
        internal DispatcherErrors() => Dispatcher.UIThread.UnhandledException += OnError;
        private void OnError(object? sender, DispatcherUnhandledExceptionEventArgs args)
        { Seen.Add(args.Exception); args.Handled = true; }
        public void Dispose() => Dispatcher.UIThread.UnhandledException -= OnError;
    }

    private sealed class SparseWriteGate : IDisposable
    {
        private readonly string _ready, _release;
        private (int Pid, string Start)? _identity;
        internal string Executable { get; }
        internal SparseWriteGate(Fixture f)
        {
            Executable = f.Output("sparse-admitted-writer.py"); _ready = Executable + ".ready"; _release = Executable + ".release";
            File.WriteAllText(Executable, $$"""
                #!/usr/bin/python3
                import json,os,subprocess,sys,time
                from pathlib import Path
                ready=Path({{JsonSerializer.Serialize(_ready)}})
                release=Path({{JsonSerializer.Serialize(_release)}})
                args=sys.argv[1:]
                if 'config' in args and '--worktree' in args and '--replace-all' in args and 'core.sparseCheckout' in args and not ready.exists():
                    result=subprocess.run(['/usr/bin/git',*args])
                    if result.returncode: sys.exit(result.returncode)
                    stat=Path('/proc/self/stat').read_text().rsplit(') ',1)[1].split()
                    pending=Path(str(ready)+'.pending')
                    pending.write_text(json.dumps({'pid':os.getpid(),'start_ticks':stat[19]}))
                    os.replace(pending,ready)
                    deadline=time.monotonic()+30
                    while not release.exists():
                        if time.monotonic()>deadline: sys.exit(124)
                        time.sleep(.01)
                    sys.exit(0)
                os.execv('/usr/bin/git',['/usr/bin/git',*args])
                """, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            { File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal bool Started => File.Exists(_ready);
        internal bool Retired
        {
            get
            {
                if (_identity is null)
                {
                    if (!Started) { return false; }
                    using JsonDocument marker = JsonDocument.Parse(File.ReadAllBytes(_ready));
                    _identity = (marker.RootElement.GetProperty("pid").GetInt32(), marker.RootElement.GetProperty("start_ticks").GetString()!);
                }
                try
                {
                    string stat = File.ReadAllText("/proc/" + _identity.Value.Pid.ToString(CultureInfo.InvariantCulture) + "/stat");
                    return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19] != _identity.Value.Start;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return true; }
                // A procfs task may vanish after open; .NET Unix exposes ESRCH as raw errno 3.
                catch (IOException error) when (OperatingSystem.IsLinux() && error.HResult == 3) { return true; }
            }
        }
        internal void Release() => File.WriteAllText(_release, "release sparse writer\n");
        public void Dispose() => Release();
    }

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        // Enter/Escape can synchronously close and dispose this headless window.
        if (window.IsVisible && window.PlatformImpl is not null) { window.KeyReleaseQwerty(key, RawInputModifiers.None); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class SparseKeyTrace : IDisposable
    {
        private readonly SparseCheckoutDialog _dialog;
        private readonly TextBox _editor;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly List<object> _records = [];
        private readonly List<(InputElement Element, EventHandler<KeyEventArgs> Handler)> _handlers = [];

        internal SparseKeyTrace(SparseCheckoutDialog dialog, TextBox editor)
        {
            _dialog = dialog; _editor = editor;
            Observe(dialog, "window.tunnel", RoutingStrategies.Tunnel);
            Observe(editor, "editor.tunnel", RoutingStrategies.Tunnel);
            Observe(editor, "editor.bubble", RoutingStrategies.Bubble);
            Observe(dialog, "window.bubble", RoutingStrategies.Bubble);
        }

        private void Observe(InputElement element, string phase, RoutingStrategies route)
        {
            EventHandler<KeyEventArgs> handler = (_, args) => Record(phase, args);
            element.AddHandler(InputElement.KeyDownEvent, handler, route, handledEventsToo: true);
            _handlers.Add((element, handler));
        }

        internal void Record(string phase, KeyEventArgs? args = null) => _records.Add(new
        {
            Phase = phase, ElapsedMilliseconds = _watch.Elapsed.TotalMilliseconds,
            Event = args?.RoutedEvent?.ToString(), Route = args?.Route.ToString(),
            Key = args?.Key.ToString(), PhysicalKey = args?.PhysicalKey.ToString(), KeySymbol = args?.KeySymbol,
            Modifiers = args?.KeyModifiers.ToString(), Handled = args?.Handled, Source = Identity(args?.Source),
            Focused = Identity(_dialog.FocusManager?.GetFocusedElement()),
            Visible = _dialog.IsVisible, Active = _dialog.IsActive, Enabled = _dialog.IsEffectivelyEnabled,
            Platform = _dialog.PlatformImpl?.GetType().FullName,
            Busy = _dialog.IsBusy, _dialog.MayHaveRepositoryChanges, _dialog.HasRepositoryChanges,
            _dialog.LastError, LastException = _dialog.LastException?.ToString(),
            Status = _dialog.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "OperationStatus")?.Text,
            EditorFocus = _editor.IsKeyboardFocusWithin, EditorEnabled = _editor.IsEffectivelyEnabled,
            EditorTextLength = _editor.Text?.Length, _editor.CaretIndex, _editor.SelectionStart, _editor.SelectionEnd
        });

        internal void CaptureFailure(Exception failure)
        {
            // Read the existing frame only: no focus repair, input, dispatcher pump, layout or render tick.
            try
            {
                Record("failure-before-cleanup");
                string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
                Directory.CreateDirectory(directory);
                string prefix = Path.Combine(directory, "sparse-ctrl-enter-failure-" + Guid.NewGuid().ToString("N"));
                string? framePath = null, frameError = null;
                try
                {
                    using var frame = _dialog.IsVisible && _dialog.PlatformImpl is not null ? _dialog.GetLastRenderedFrame() : null;
                    if (frame is null) { frameError = "No existing headless frame is available."; }
                    else { framePath = prefix + ".png"; frame.Save(framePath); TestContext.AddTestAttachment(framePath); }
                }
                catch (Exception error) { frameError = error.ToString(); }
                string path = prefix + ".json";
                File.WriteAllText(path, JsonSerializer.Serialize(new { Failure = failure.ToString(), FramePath = framePath, FrameError = frameError, Records = _records },
                    new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                TestContext.AddTestAttachment(path);
                TestContext.Progress.WriteLine("SPARSE-CTRL-ENTER passive diagnostic: " + path);
            }
            catch (Exception error) { TestContext.Progress.WriteLine("SPARSE-CTRL-ENTER diagnostic failed: " + error); }
        }

        private static string Identity(object? element) => element is Control control
            ? control.GetType().FullName + "/" + control.Name : element?.GetType().FullName ?? "<none>";
        public void Dispose()
        { foreach ((InputElement element, EventHandler<KeyEventArgs> handler) in _handlers) { element.RemoveHandler(InputElement.KeyDownEvent, handler); } }
    }
    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); if (window.IsVisible) { window.UpdateLayout(); } AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string name)
    {
        Pump(window); using var frame = window.CaptureRenderedFrame() ?? throw new AssertionException("No Sparse frame.");
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
    }

    private static async Task<Fixture> CreateAsync()
    {
        Fixture f = await Fixture.CreateAsync();
        foreach (string directory in new[] { "included", "excluded" })
        { Directory.CreateDirectory(f.PathInRepository(directory)); await File.WriteAllTextAsync(f.PathInRepository(directory + "/file.txt"), directory + "\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Sparse UI fixture"); return f;
    }
    private static async Task<SparseCheckoutDialog> OpenAsync(GitRepository repository)
    { SparseCheckoutDialog dialog = new(repository); dialog.Show(); await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString()); return dialog; }
    private static void Edit(SparseCheckoutDialog dialog, bool cone)
    {
        Field<CheckBox>(dialog, "SparseEnabled").IsChecked = true; Field<CheckBox>(dialog, "SparseCone").IsChecked = cone;
        Field<CheckBox>(dialog, "SparseIndex").IsChecked = cone; Field<TextBox>(dialog, "SparseRules").Text = cone ? "included\n" : "/included/**\n";
    }
    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pointer(Window window, string name) => PushProcessTestDriver.Pointer(Field<Button>(window, name));
    private static async Task<Window> QuestionAsync(SparseCheckoutDialog dialog)
    { await Until(() => dialog.OwnedWindows.Any(window => window.Name == "SaveSparseQuestion")); return dialog.OwnedWindows.Single(window => window.Name == "SaveSparseQuestion"); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "The sparse editor did not settle within 20 seconds.");
    }
    private static async Task CloseAsync(SparseCheckoutDialog dialog)
    {
        if (!dialog.IsVisible) { return; }
        foreach (Window child in dialog.OwnedWindows.ToArray())
        { if (child.Name == "SaveSparseQuestion") { Pointer(child, "SparseSaveNo"); } else { child.Close(); } }
        dialog.Close();
        await Until(() => !dialog.IsVisible || dialog.OwnedWindows.Any(window => window.Name == "SaveSparseQuestion"));
        if (dialog.IsVisible) { Pointer(await QuestionAsync(dialog), "SparseSaveNo"); }
        await Until(() => !dialog.IsVisible && !dialog.IsBusy);
    }
}
