using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>One set of confirmations and owned native processes for the manager, tree and toolbar.</summary>
internal static class WorktreeDialogControls
{
    internal static Control ButtonContent(object caption, string? icon = null)
    {
        Control label = caption as Control ?? new TextBlock { Text = caption.ToString(), VerticalAlignment = VerticalAlignment.Center };
        if (icon is null) { return label; }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center,
            Children = { ResetDialogControls.Icon(icon), label } };
    }

    internal static async Task<bool> ConfirmOpenAsync(Window owner, GitRepository repository, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return false; }
        WorktreeSnapshot expected = await SnapshotAsync(repository, path, token);
        if (!expected.CanOpen) { return false; }
        BrowseCommandPreferencesStore store = new();
        if (!store.Load().DontConfirmSwitchWorktree)
        {
            TranslationCatalog translations = TranslationCatalog.LoadDefault();
            Window prompt = Prompt(owner, "SwitchWorktreeConfirmation", Text(translations, "_switchWorktreeCaption", "Open worktree"));
            CheckBox dontShow = new() { Name = "WorktreeDontShowAgain", MinHeight = 24,
                Content = new AccessText { Text = translations.TranslateAccessKeys("TranslatedStrings", "_dontShowAgain", "Text", "&Don't show me this message again") } };
            Button yes = Choice(translations, "ConfirmSwitchWorktree", "Yes", true), no = Choice(translations, "CancelSwitchWorktree", "No", false);
            no.IsCancel = true; yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
            prompt.Content = new StackPanel { Margin = new Thickness(12), Spacing = 12, Children =
            {
                new TextBlock { Name = "SwitchWorktreeMessage", Text = string.Format(CultureInfo.CurrentCulture,
                    Text(translations, "_switchWorktreeConfirmation", "Switch to worktree at {0}?"), ExportDialogControls.DisplayPath(expected.Path)), TextWrapping = TextWrapping.Wrap },
                dontShow, ExportDialogControls.Buttons(yes, no)
            } };
            using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(() => { if (prompt.IsVisible) { prompt.Close(false); } }));
            bool accepted;
            try { accepted = await prompt.ShowDialog<bool>(owner); }
            finally { prompt.Close(); }
            // The original suppressible confirmation persists the checkbox even after No.
            if (dontShow.IsChecked == true) { store.Save(store.Load() with { DontConfirmSwitchWorktree = true }); }
            token.ThrowIfCancellationRequested(); if (!accepted || !owner.IsVisible) { return false; }
        }
        await repository.ValidateWorktreeOpenAsync(expected, token);
        return owner.IsVisible;
    }

    /// <returns>True after a command starts, including a native failure or cancellation requiring a reread.</returns>
    internal static async Task<bool> DeleteAsync(Window owner, GitRepository repository, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return false; }
        WorktreeSnapshot expected = await SnapshotAsync(repository, path, token);
        if (!expected.CanRemove) { throw new InvalidOperationException("The main, current or missing worktree cannot be deleted here."); }
        TranslationCatalog translations = TranslationCatalog.LoadDefault();
        Window prompt = Prompt(owner, "DeleteWorktreeConfirmation", Text(translations, "_deleteWorktreeCaption", "Delete worktree"));
        ClassicChoiceAppearance.Apply(prompt, "deleteWorktree");
        CheckBox discard = new() { Name = "DiscardWorktreeChanges", MinHeight = 24, Content = translations.Translate("Discard tracked and untracked changes (including submodules)") };
        CheckBox unlock = new() { Name = "OverrideWorktreeLock", MinHeight = 24, IsVisible = expected.IsLocked,
            Content = translations.Translate("Override this worktree's lock") };
        TextBlock reason = new() { Name = "WorktreeLockReason", IsVisible = expected.IsLocked, TextWrapping = TextWrapping.Wrap,
            Text = translations.Translate("Locked") + ": " + (string.IsNullOrEmpty(expected.LockedReason) ? translations.Translate("No reason given") : expected.LockedReason) };
        TextBlock lockRemoval = new() { Name = "WorktreeLockRemovalNotice", IsVisible = expected.IsLocked, TextWrapping = TextWrapping.Wrap,
            Text = translations.Translate("Git removes a locked worktree with a force option that also discards changes. Confirm both options to continue.") };
        Button yes = Choice(translations, "ConfirmDeleteWorktree", "Yes", false), no = Choice(translations, "CancelDeleteWorktree", "No", true);
        no.IsCancel = true; yes.IsEnabled = !expected.IsLocked;
        void UpdateAccept() => yes.IsEnabled = !expected.IsLocked || unlock.IsChecked == true && discard.IsChecked == true;
        unlock.IsCheckedChanged += (_, _) => UpdateAccept(); discard.IsCheckedChanged += (_, _) => UpdateAccept();
        yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children =
        {
            new TextBlock { Text = Text(translations, "_cannotBeUndone", "This action cannot be undone."), FontWeight = FontWeight.SemiBold },
            new TextBlock { Name = "DeleteWorktreeMessage", Text = string.Format(CultureInfo.CurrentCulture,
                Text(translations, "_deleteWorktreeConfirmation", "Are you sure you want to delete the worktree at {0}?"), ExportDialogControls.DisplayPath(expected.Path)), TextWrapping = TextWrapping.Wrap },
            discard, reason, unlock, lockRemoval, ExportDialogControls.Buttons(yes, no)
        } };
        using CancellationTokenRegistration registration = token.Register(() => Dispatcher.UIThread.Post(() => { if (prompt.IsVisible) { prompt.Close(false); } }));
        bool accepted;
        try { accepted = await prompt.ShowDialog<bool>(owner); }
        finally { prompt.Close(); }
        token.ThrowIfCancellationRequested(); if (!accepted || !owner.IsVisible) { return false; }
        GitProcessDialogResult result = await GitProcessDialog.ShowAsync(owner, repository.Path, "git worktree remove " + ExportDialogControls.DisplayPath(expected.Path), true,
            async (ct, output) => (await repository.DeleteWorktreeAsync(expected, discard.IsChecked == true, unlock.IsChecked == true, ct, output)).Result, token);
        return result.CommandStarted;
    }

    internal static async Task<bool> PruneAsync(Window owner, GitRepository repository, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!owner.IsVisible) { return false; }
        GitProcessDialogResult result = await GitProcessDialog.ShowAsync(owner, repository.Path, "git worktree prune", true,
            (ct, output) => repository.PruneWorktreesAsync(ct, output), token);
        return result.CommandStarted;
    }

    private static async Task<WorktreeSnapshot> SnapshotAsync(GitRepository repository, string path, CancellationToken token)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, repository.Path));
        return (await repository.GetWorktreeSnapshotsAsync(token)).FirstOrDefault(worktree => Path.TrimEndingDirectorySeparator(worktree.Path) == fullPath)
            ?? throw new InvalidOperationException("The worktree registration changed. Refresh the worktree list.");
    }

    private static string Text(TranslationCatalog translations, string item, string source) => translations.Translate("TranslatedStrings", item, "Text", source);
    private static Window Prompt(Window owner, string name, string title) => new()
    {
        Name = name, Title = title, Width = 540, MinWidth = 420, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = owner.ActualThemeVariant
    };
    private static Button Choice(TranslationCatalog translations, string name, string caption, bool isDefault)
        => new() { Name = name, Content = translations.Translate(caption), MinWidth = 75, Height = 25, IsDefault = isDefault };
}
