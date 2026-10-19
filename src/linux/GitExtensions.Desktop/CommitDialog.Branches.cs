using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using GitExtensions.Linux.Desktop.Controls;
using GitCommands.Git;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private readonly TextBlock _branchName = new() { Name = "CommitBranchName", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _branchRemote = new()
    {
        Name = "CommitBranchRemote", Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _commitDetails = new() { Name = "CommitDetails", VerticalAlignment = VerticalAlignment.Center };
    private string? _statusLocalBranch;

    private MenuItem CreateBranchToolbarItem()
    {
        MenuItem create = new() { Name = "CommitCreateBranch", Header = IconLabel(C("createBranchToolStripButton", "Create branch"), "BranchCreate.png") };
        DesktopHotkeys.BindToolTip(create, "Commit", "CreateBranch", C("createBranchToolStripButton", "Create branch"));
        create.Click += async (_, _) => { if (!IsBusy && !_confirmationOpen) { await CreateBranchAsync(); } };
        return create;
    }

    private Control BuildCommitStatusBar()
    {
        _branchRemote.Bind(ForegroundProperty, new DynamicResourceExtension("SystemControlForegroundAccentBrush"));
        // LinkLabel.HoverUnderline: no ordinary Fluent button chrome, while Tab,
        // Enter and the visible keyboard-focus cue still belong to the native Button.
        _branchRemote.Template = new FuncControlTemplate<Button>((_, _) =>
        {
            TextBlock text = new() { Name = "CommitBranchRemoteText", VerticalAlignment = VerticalAlignment.Center };
            text.Bind(TextBlock.TextProperty, new TemplateBinding(ContentControl.ContentProperty), BindingPriority.Template);
            text.Bind(TextBlock.ForegroundProperty, new TemplateBinding(ForegroundProperty), BindingPriority.Template);
            return new Border { Name = "CommitBranchRemoteFocus", BorderThickness = new Thickness(1), Background = Brushes.Transparent, Child = text };
        });
        Styles.Add(new Style(selector => selector.OfType<Button>().Name("CommitBranchRemote").Class(":pointerover").Template().OfType<TextBlock>().Name("CommitBranchRemoteText"))
        { Setters = { new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline) } });
        Styles.Add(new Style(selector => selector.OfType<Button>().Name("CommitBranchRemote").Template().OfType<Border>().Name("CommitBranchRemoteFocus"))
        { Setters = { new Setter(Border.BorderBrushProperty, Brushes.Transparent) } });
        Styles.Add(new Style(selector => selector.OfType<Button>().Name("CommitBranchRemote").Class(":focus-visible").Template().OfType<Border>().Name("CommitBranchRemoteFocus"))
        { Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicHighlightBrush")) } });
        _branchRemote.Click += async (_, _) => { if (!IsBusy && !_confirmationOpen) { await RunAsync(T("Remote repositories"), OpenCommitTrackingAsync); } };
        _interactive.Add(_branchRemote);
        StackPanel branch = new() { Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { OriginalMenuItems.Icon("Branch.png"), _branchName, _branchRemote } };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _state, branch, _commitDetails } };
    }

    private async Task RefreshBranchStatusAsync()
    {
        string name = _status?.IsDetached == true ? DetachedHeadParser.DetachedBranch : _status?.Branch ?? "";
        GitBranchTrackingSnapshot? branch = (await _repository.GetBranchTrackingSettingsAsync(OperationToken))
            .FirstOrDefault(value => value.LocalBranch == name);
        _statusLocalBranch = branch?.LocalBranch;
        _branchName.Text = name + (branch is null ? "" : " →");
        _branchRemote.IsVisible = branch is not null;
        if (branch is null) { _branchRemote.Content = null; return; }
        string destination;
        if (!string.IsNullOrEmpty(branch.Remote)) { destination = branch.Remote + "/" + branch.MergeBranch; }
        else
        {
            IReadOnlyList<GitRemote> remotes = await _repository.GetRemotesAsync(OperationToken);
            string? remote = remotes.FirstOrDefault(value => value.Name == "origin")?.Name
                ?? remotes.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal).FirstOrDefault();
            destination = remote is null ? C("_statusBarBranchWithoutRemote", "(remote not configured)")
                : remote + "/" + name + " " + C("_untrackedRemote", "(untracked)");
        }
        _branchRemote.Content = destination;
        Title = string.Format(C("_formTitle", "Commit to {0} ({1})"), name, _repository.Path);
    }

    private async Task OpenCommitTrackingAsync()
    {
        string? branch = _statusLocalBranch;
        if (branch is null) { return; }
        RemotesDialog dialog = new(_repository, preselectLocalBranch: branch) { RequestedThemeVariant = ActualThemeVariant };
        await ShowCommitBranchDialogAsync(dialog);
        if (dialog.HasRepositoryChanges) { HasRepositoryChanges = true; RepositoryChanged?.Invoke(this, EventArgs.Empty); }
        // Tracking edits do not reload the two file lists, the diff, or the message editor.
        _status = await _repository.GetStatusAsync(OperationToken);
        await RefreshBranchStatusAsync();
    }

    private async Task<bool> OpenCreateCommitBranchAsync(string revision = "HEAD")
    {
        DesktopPreferences preferences = new PreferencesStore().Load();
        CreateBranchDialog dialog = new(_repository, revision, autoNormalize: preferences.AutoNormaliseBranchName, replacement: preferences.AutoNormaliseSymbol)
            { RequestedThemeVariant = ActualThemeVariant };
        await ShowCommitBranchDialogAsync(dialog);
        if (dialog.HasRepositoryChanges)
        {
            HasRepositoryChanges = true;
            await RefreshDataAsync();
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        return dialog.CreatedBranch is not null;
    }

    private async Task ShowCommitBranchDialogAsync(RepositoryDialog dialog)
    {
        Task initialization = Task.CompletedTask;
        dialog.Opened += (_, _) => initialization = dialog.InitializeAsync();
        using CancellationTokenRegistration registration = OperationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try { await dialog.ShowDialog(this); await initialization; OperationToken.ThrowIfCancellationRequested(); }
        finally { dialog.Close(); }
        ScriptMayHaveChangedRepository |= dialog.ScriptMayHaveChangedRepository;
    }

    // Called within the existing commit operation, after optional child staging and before
    // saving the attempted message or running BeforeCommit. It must not reenter RunAsync.
    private async Task<bool> EnsureCommitBranchAsync()
    {
        return await CommitBranchGuard.EnsureAsync(this, _repository, _preferencesStore, _translations, OperationToken,
            () => OpenCreateCommitBranchAsync((_kind.SelectedIndex != 0 ? _target.SelectedItem as GitCommit : null)?.Hash ?? "HEAD"),
            CheckoutCommitBranchAsync);
    }

    private async Task<bool> CheckoutCommitBranchAsync()
    {
        GitCommit? edited = _kind.SelectedIndex != 0 ? _target.SelectedItem as GitCommit : null;
        DesktopPreferences preferences = new PreferencesStore().Load();
        CheckoutBranchConstraint? constraint = edited is null ? null : new(
            token => CommitCheckoutReferencesAsync(edited.Hash, token),
            async (branch, options, token) =>
            {
                await RequireCommitCheckoutReferenceAsync(edited.Hash, branch, token);
                return await _repository.CheckoutAsync(options, token);
            })
        {
            AllowDefaultAction = true,
            PrepareAsync = async (branch, options, token) =>
            {
                await RequireCommitCheckoutReferenceAsync(edited.Hash, branch, token);
                return await _repository.PrepareCheckoutAsync(options, token);
            },
            CompleteAsync = async (branch, preparation, token) =>
            {
                await RequireCommitCheckoutReferenceAsync(edited.Hash, branch, token);
                return await _repository.CheckoutPreparedAsync(preparation, token);
            }
        };
        CheckoutBranchDialog? checkout = null;
        checkout = new(_repository, autoNormalize: preferences.AutoNormaliseBranchName, replacement: preferences.AutoNormaliseSymbol,
            afterSubmoduleUpdateAsync: _afterSubmoduleUpdateAsync is null ? null : token => _afterSubmoduleUpdateAsync(checkout!.IsVisible ? checkout! : this, token), constraint: constraint)
            { RequestedThemeVariant = ActualThemeVariant };
        try { await OpenCommitCheckoutDialogAsync(checkout); }
        finally
        {
            ScriptMayHaveChangedRepository |= checkout.ScriptMayHaveChangedRepository;
            if (checkout.HasRepositoryChanges)
            {
                HasRepositoryChanges = true;
                if (!OperationToken.IsCancellationRequested) { await RefreshDataAsync(); }
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        return checkout.Succeeded;
    }

    private async Task OpenCommitCheckoutDialogAsync(CheckoutBranchDialog checkout)
    {
        using CancellationTokenRegistration registration = OperationToken.Register(() => Dispatcher.UIThread.Post(checkout.Close));
        try
        {
            await checkout.InitializeAsync(OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            if (checkout.CanExecuteDefault)
            {
                await checkout.ExecuteDefaultAsync(this, OperationToken);
                OperationToken.ThrowIfCancellationRequested();
                if (checkout.LastError is null) { return; }
            }
            // A failed default action keeps the same picker and its error/stash/
            // selection state. A veto or declined decision has already returned.
            await checkout.ShowDialog(this);
            OperationToken.ThrowIfCancellationRequested();
        }
        finally { checkout.Close(); }
    }

    private async Task<IReadOnlyList<GitReference>> CommitCheckoutReferencesAsync(string editedHash, CancellationToken token)
    {
        GitCommitContainment containment = await _repository.GetCommitContainingReferencesAsync(editedHash,
            GitCommitReferenceScope.LocalBranches | GitCommitReferenceScope.RemoteBranches, token);
        Dictionary<string, string> containing = containment.References.ToDictionary(reference => reference.FullName, reference => reference.ObjectHash, StringComparer.Ordinal);
        return (await _repository.GetReferencesAsync(token)).Where(reference => reference.SymbolicTarget is null
            && containing.TryGetValue(reference.Name, out string? hash) && hash == reference.Hash).ToArray();
    }

    private async Task RequireCommitCheckoutReferenceAsync(string editedHash, GitReference expected, CancellationToken token)
    {
        if (!(await CommitCheckoutReferencesAsync(editedHash, token)).Any(reference => reference.Name == expected.Name && reference.Hash == expected.Hash))
        { throw new InvalidOperationException(T("The selected branch changed or no longer contains the edited commit. Reload the checkout dialog.")); }
    }
}

internal enum CommitBranchChoice { Cancel, Checkout, Create, Continue }

/// <summary>The original detached-HEAD decision keeps Cancel as the default action.</summary>
internal sealed class CommitBranchChoiceDialog : Window
{
    internal CommitBranchChoiceDialog(Localization.TranslationCatalog translations)
    {
        string T(string source) => translations.Translate(source);
        Name = "CommitBranchChoice";
        Title = T("Not on a branch"); Width = 570; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ClassicCommandLink.Configure(this, "commit-branch-command-link", 48d, disabledForeground: true);
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("32,12,*"), Margin = new Thickness(18, 18, 18, 12) };
        heading.Children.Add(ClassicCommandLink.ErrorIcon(VerticalAlignment.Top));
        StackPanel instructions = new() { Spacing = 10, Children =
        {
            new TextBlock { Text = T("You are not working on a branch"), FontSize = DesktopFonts.For(DesktopFontRole.Application).SizeInDips + 3, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = translations.Translate("FormCommit", "_notOnBranch", "Text", "This commit will be unreferenced when switching to another branch and can be lost.\n\nDo you want to continue?"), TextWrapping = TextWrapping.Wrap }
        } };
        Grid.SetColumn(instructions, 2); heading.Children.Add(instructions);
        StackPanel actions = new() { Margin = new Thickness(48, 0, 12, 12), Spacing = 2 };
        foreach ((CommitBranchChoice choice, string caption) in new[]
        {
            (CommitBranchChoice.Checkout, "Checkout branch"), (CommitBranchChoice.Create, "Create branch"), (CommitBranchChoice.Continue, "Continue")
        })
        {
            Button action = new() { Name = "Detached" + choice, Content = T(caption), HorizontalContentAlignment = HorizontalAlignment.Left };
            ClassicCommandLink.Apply(action, "commit-branch-command-link");
            action.Click += (_, _) => Close(choice); actions.Children.Add(action);
        }
        Button cancel = new() { Name = "DetachedCancel", Content = T("Cancel"), IsCancel = true, IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 75 };
        cancel.Click += (_, _) => Close(CommitBranchChoice.Cancel);
        Border footer = new() { Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0), Child = cancel };
        footer.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ClassicMenuBrush"));
        footer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ClassicBorderBrush"));
        Content = new StackPanel { Children = { heading, actions, footer } };
        Opened += (_, _) => { cancel.Focus(); ModalWindowActivation.AfterMapping(this, () => IsVisible); };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(CommitBranchChoice.Cancel); } };
    }
}
